window.netratelRemoteSupport = (() => {
    const protocolRevision = 1;
    const featureSet = "control_ack,fresh_login_session,current_peer_input,first_frame_gate,capture_recovery,desktop_context_recovery";
    const buildCommit = "0.4.92-desktop-context-recovery";
    const assetUrl = document.currentScript?.src ?? "unknown";
    console.info("[RemoteSupport] remote-support-dialog.js loaded", { protocolRevision, assetUrl });
    const sessions = new Map();

    function capabilities() {
        return {
            protocolRevision,
            featureSet,
            buildCommit,
            assetUrl,
            setControlEnabled: true,
            mouseControl: true,
            keyboardControl: true,
            qualityProfiles: true,
            mediaStats: true,
            sasControl: true,
            inputAcknowledgements: true,
            inputProbe: true
        };
    }

    async function create(dotNetRef, sessionId, videoElementId, iceServers, providerGeneration, targetWindowsSessionId) {
        await closePeer(sessionId);
        const generation = Number(providerGeneration || 1);
        const configuredIceServers = normalizeIceServers(iceServers);
        const pc = new RTCPeerConnection({
            iceServers: configuredIceServers,
            iceCandidatePoolSize: 2
        });
        const channel = pc.createDataChannel("netratel-control", { ordered: true });
        const video = document.getElementById(videoElementId);
        const session = {
            sessionId,
            pc,
            channel,
            video,
            dotNetRef,
            generation,
            targetWindowsSessionId: targetWindowsSessionId === null || targetWindowsSessionId === undefined || targetWindowsSessionId === ""
                ? null
                : (Number.isFinite(Number(targetWindowsSessionId)) ? Number(targetWindowsSessionId) : null),
            peerInstanceId: createInputEventId(),
            pendingInputAcks: new Map(),
            pendingCandidates: [],
            controlEnabled: false,
            profile: "low",
            pendingQualityProfile: null,
            renderedFrames: 0,
            firstFrameReported: false,
            lastFrameStatsAt: performance.now(),
            renderedFps: 0,
            lastInboundBytes: null,
            lastInboundTimestamp: null,
            inboundKbps: null,
            mouseSent: 0,
            keyboardSent: 0,
            mouseMoveReceived: 0,
            mouseMoveSent: 0,
            mouseMoveCoalesced: 0,
            mouseClickSent: 0,
            pendingMouseMove: null,
            mouseMoveTimer: null,
            lastFrameRenderedAt: null,
            frameFallbackHandler: null,
            frameFallbackTimer: null,
            lastControlError: null
        };
        sessions.set(sessionId, session);

        pc.ontrack = async event => {
            if (sessions.get(sessionId) !== session) return;
            if (video) {
                video.tabIndex = 0;
                video.srcObject = event.streams?.[0] || new MediaStream([event.track]);
                startVideoFrameStats(session);
                startStatsTimer(sessionId, session);
                try { await video.play(); } catch { }
            }

            await dotNetRef.invokeMethodAsync("OnRemoteTrack", generation, event.track?.kind || "media");
        };

        pc.onicecandidate = async event => {
            if (sessions.get(sessionId) !== session) return;
            if (event.candidate) {
                await dotNetRef.invokeMethodAsync("OnLocalIceCandidate", generation, JSON.stringify(event.candidate));
            } else {
                await dotNetRef.invokeMethodAsync("OnIceStateChanged", generation, "candidate gathering complete");
            }
        };

        pc.onconnectionstatechange = async () => {
            if (sessions.get(sessionId) !== session) return;
            await dotNetRef.invokeMethodAsync("OnPeerStateChanged", generation, pc.connectionState || "unknown");
        };

        pc.oniceconnectionstatechange = async () => {
            if (sessions.get(sessionId) !== session) return;
            await dotNetRef.invokeMethodAsync("OnIceStateChanged", generation, pc.iceConnectionState || "unknown");
        };

        pc.onicegatheringstatechange = async () => {
            if (sessions.get(sessionId) !== session) return;
            await dotNetRef.invokeMethodAsync("OnIceStateChanged", generation, `gathering ${pc.iceGatheringState || "unknown"}`);
        };

        channel.onopen = async () => {
            if (sessions.get(sessionId) !== session) return;
            if (session.pendingQualityProfile) {
                sendControl(session, session.pendingQualityProfile);
                session.pendingQualityProfile = null;
            }

            await dotNetRef.invokeMethodAsync("OnDataChannelStateChanged", generation, "open");
        };

        channel.onclose = async () => {
            if (sessions.get(sessionId) !== session) return;
            await dotNetRef.invokeMethodAsync("OnDataChannelStateChanged", generation, "closed");
        };

        channel.onmessage = event => handleInputAcknowledgement(session, event?.data);

        pc.addTransceiver("video", { direction: "recvonly" });
        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);
        return JSON.stringify(offer);
    }

    function setControlEnabled(sessionId, enabled) {
        const session = sessions.get(sessionId);
        if (!session || !session.video) {
            return;
        }

        session.controlEnabled = !!enabled;
        if (enabled) {
            attachPointerHandlers(session);
            attachKeyboardHandlers(session);
            try { session.video.focus({ preventScroll: true }); } catch { try { session.video.focus(); } catch { } }
        } else {
            detachPointerHandlers(session);
            detachKeyboardHandlers(session);
        }
    }

    function setQualityProfile(sessionId, profile) {
        const session = sessions.get(sessionId);
        if (!session) {
            return;
        }

        const normalized = normalizeQualityProfile(profile);
        session.profile = normalized.profile;
        const payload = {
            type: "quality_profile",
            profile: normalized.profile,
            maxWidth: normalized.maxWidth,
            maxHeight: normalized.maxHeight,
            fps: normalized.fps,
            targetKbps: normalized.targetKbps
        };

        if (session.channel?.readyState === "open") {
            sendControl(session, payload);
            session.pendingQualityProfile = null;
        } else {
            session.pendingQualityProfile = payload;
        }
    }

    function refreshVideo(sessionId) {
        const session = sessions.get(sessionId);
        if (!session) {
            return;
        }

        sendControl(session, { type: "refresh_video" });
    }

    function sendSas(sessionId) {
        const session = sessions.get(sessionId);
        if (!session) {
            return false;
        }

        return sendControl(session, {
            type: "sas",
            action: "send_ctrl_alt_del"
        });
    }

    function attachPointerHandlers(session) {
        if (session.pointerHandlersAttached || !session.video) {
            return;
        }

        const video = session.video;
        session.pointerMove = ev => sendPointer(session, "mouse_move", ev);
        session.pointerDown = ev => {
            video.setPointerCapture?.(ev.pointerId);
            try { video.focus({ preventScroll: true }); } catch { try { video.focus(); } catch { } }
            sendPointer(session, "mouse_down", ev);
        };
        session.pointerUp = ev => sendPointer(session, "mouse_up", ev);
        session.contextMenu = ev => {
            if (session.controlEnabled) {
                ev.preventDefault();
            }
        };
        session.wheel = ev => {
            if (!session.controlEnabled) {
                return;
            }

            ev.preventDefault();
            const point = relativeVideoPoint(video, ev);
            if (!point) {
                return;
            }

            sendControl(session, {
                type: "mouse_wheel",
                x: point.x,
                y: point.y,
                deltaX: Math.round(ev.deltaX || 0),
                deltaY: Math.round(ev.deltaY || 0),
                browserEventCreatedAt: new Date().toISOString()
            }) && notifyControlInputSent(session, "mouse", "click");
        };

        video.addEventListener("pointermove", session.pointerMove);
        video.addEventListener("pointerdown", session.pointerDown);
        video.addEventListener("pointerup", session.pointerUp);
        video.addEventListener("contextmenu", session.contextMenu);
        video.addEventListener("wheel", session.wheel, { passive: false });
        session.pointerHandlersAttached = true;
    }

    function detachPointerHandlers(session) {
        if (!session.pointerHandlersAttached || !session.video) {
            return;
        }

        const video = session.video;
        video.removeEventListener("pointermove", session.pointerMove);
        video.removeEventListener("pointerdown", session.pointerDown);
        video.removeEventListener("pointerup", session.pointerUp);
        video.removeEventListener("contextmenu", session.contextMenu);
        video.removeEventListener("wheel", session.wheel);
        if (session.mouseMoveTimer) {
            clearTimeout(session.mouseMoveTimer);
            session.mouseMoveTimer = null;
        }
        session.pointerHandlersAttached = false;
    }

    function attachKeyboardHandlers(session) {
        if (session.keyboardHandlersAttached || !session.video) {
            return;
        }

        const video = session.video;
        session.keyDown = ev => sendKeyboard(session, "key_down", ev);
        session.keyUp = ev => sendKeyboard(session, "key_up", ev);
        video.addEventListener("keydown", session.keyDown);
        video.addEventListener("keyup", session.keyUp);
        session.keyboardHandlersAttached = true;
    }

    function detachKeyboardHandlers(session) {
        if (!session.keyboardHandlersAttached || !session.video) {
            return;
        }

        const video = session.video;
        video.removeEventListener("keydown", session.keyDown);
        video.removeEventListener("keyup", session.keyUp);
        session.keyboardHandlersAttached = false;
    }

    function sendPointer(session, type, ev) {
        if (!session.controlEnabled) {
            return;
        }

        ev.preventDefault();
        const point = relativeVideoPoint(session.video, ev);
        if (!point) {
            return;
        }

        const payload = {
            type,
            x: point.x,
            y: point.y,
            button: normalizeMouseButton(ev.button ?? 0),
            browserEventCreatedAt: new Date().toISOString()
        };
        if (type === "mouse_move") {
            queueMouseMove(session, payload);
            return;
        }

        sendControl(session, payload) && notifyControlInputSent(session, "mouse", "click");
    }

    function queueMouseMove(session, payload) {
        session.mouseMoveReceived++;
        if (session.pendingMouseMove) {
            session.mouseMoveCoalesced++;
        }

        session.pendingMouseMove = payload;
        if (session.mouseMoveTimer) {
            return;
        }

        session.mouseMoveTimer = setTimeout(() => {
            session.mouseMoveTimer = null;
            const latest = session.pendingMouseMove;
            session.pendingMouseMove = null;
            if (latest) {
                latest.mouseMoveCoalescedCount = session.mouseMoveCoalesced;
            }

            if (latest && sendControl(session, latest)) {
                notifyControlInputSent(session, "mouse", "move");
            }
        }, 33);
    }

    function normalizeMouseButton(domButton) {
        if (domButton === 2) {
            return 1;
        }

        if (domButton === 1) {
            return 2;
        }

        return 0;
    }

    function sendKeyboard(session, type, ev) {
        if (!session.controlEnabled) {
            return;
        }

        if ((ev.altKey && ev.key === "Tab") || ev.metaKey) {
            return;
        }

        if (shouldPreventKeyDefault(ev)) {
            ev.preventDefault();
        }

        const payload = {
            type,
            key: ev.key || "",
            code: ev.code || "",
            ctrlKey: !!ev.ctrlKey,
            shiftKey: !!ev.shiftKey,
            altKey: !!ev.altKey,
            metaKey: !!ev.metaKey,
            inputKind: "keyboard",
            keyCategory: categorizeKey(ev),
            browserEventCreatedAt: new Date().toISOString()
        };
        sendControl(session, payload) && notifyControlInputSent(session, "keyboard", "key");
    }

    function shouldPreventKeyDefault(ev) {
        if (ev.altKey && ev.key === "Tab") {
            return false;
        }

        const navigationKeys = new Set([
            " ", "Spacebar", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
            "Backspace", "Tab", "PageUp", "PageDown", "Home", "End", "Delete",
            "Enter", "Escape"
        ]);
        return navigationKeys.has(ev.key) || ev.ctrlKey || ev.metaKey || ev.altKey;
    }

    function sendControl(session, payload) {
        if (sessions.get(session.sessionId) !== session ||
            session.pc?.connectionState === "closed" ||
            session.channel?.readyState !== "open") {
            return false;
        }

        try {
            if (isInputPayload(payload)) {
                payload.inputEventId ||= createInputEventId();
                payload.browserEventCreatedAt ||= new Date().toISOString();
                payload.browserSentAt ||= payload.browserEventCreatedAt;
                payload.dataChannelSentAt = new Date().toISOString();
                payload.inputKind ||= payload.type?.startsWith("key_") ? "keyboard" : "mouse";
                payload.inputCategory ||= payload.keyCategory || categorizeInputType(payload.type);
                payload.remoteSupportSessionId = session.sessionId;
                payload.targetWindowsSessionId = session.targetWindowsSessionId;
                payload.peerInstanceId = session.peerInstanceId;
                payload.dataChannelId = session.channel?.id === null || session.channel?.id === undefined
                    ? null
                    : String(session.channel.id);
                payload.dataChannelLabel = session.channel?.label || "netratel-control";
                payload.dataChannelState = session.channel?.readyState || "unknown";
            }

            session.channel.send(JSON.stringify(payload));
            if (isInputPayload(payload)) {
                trackPendingInputAcknowledgement(session, payload);
                console.debug("remote_input_browser_sent", {
                    remoteSupportSessionId: session.sessionId,
                    targetWindowsSessionId: session.targetWindowsSessionId,
                    peerInstanceId: session.peerInstanceId,
                    dataChannelId: payload.dataChannelId,
                    dataChannelLabel: payload.dataChannelLabel,
                    dataChannelState: payload.dataChannelState,
                    inputEventId: payload.inputEventId,
                    category: payload.inputCategory,
                    browserEventCreatedAt: payload.browserEventCreatedAt,
                    browserSentAt: payload.dataChannelSentAt
                });
            }
            session.lastControlError = null;
            return true;
        } catch (err) {
            session.lastControlError = err?.message || "control send failed";
            try {
                session.dotNetRef?.invokeMethodAsync("OnControlInputError", session.generation || 1, session.lastControlError);
            } catch { }
            return false;
        }
    }

    function notifyControlInputSent(session, kind, detail) {
        if (kind === "keyboard") {
            session.keyboardSent++;
        } else if (kind === "mouse") {
            session.mouseSent++;
            if (detail === "move") {
                session.mouseMoveSent++;
            } else {
                session.mouseClickSent++;
            }
        }

        try {
            session.dotNetRef?.invokeMethodAsync(
                "OnControlInputSent",
                session.generation || 1,
                kind,
                session.mouseSent,
                session.keyboardSent,
                session.mouseMoveReceived,
                session.mouseMoveSent,
                session.mouseMoveCoalesced,
                session.mouseClickSent);
        } catch { }
    }

    function isInputPayload(payload) {
        return typeof payload?.type === "string" &&
            (payload.type.startsWith("mouse_") || payload.type.startsWith("key_"));
    }

    function createInputEventId() {
        try {
            return crypto.randomUUID();
        } catch {
            return `input-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
        }
    }

    function trackPendingInputAcknowledgement(session, payload) {
        const existing = session.pendingInputAcks.get(payload.inputEventId);
        if (existing) {
            clearTimeout(existing);
        }

        const timeout = setTimeout(() => {
            session.pendingInputAcks.delete(payload.inputEventId);
            if (sessions.get(session.sessionId) === session) {
                session.dotNetRef?.invokeMethodAsync(
                    "OnControlInputAcknowledgementTimedOut",
                    session.generation || 1,
                    payload.inputEventId);
            }
        }, 5000);
        session.pendingInputAcks.set(payload.inputEventId, timeout);
    }

    function handleInputAcknowledgement(session, value) {
        if (sessions.get(session.sessionId) !== session) {
            return;
        }

        let acknowledgement;
        try {
            acknowledgement = typeof value === "string" ? JSON.parse(value) : value;
        } catch {
            return;
        }

        if (!acknowledgement || acknowledgement.type !== "input_ack" || !acknowledgement.inputEventId) {
            return;
        }

        if (acknowledgement.peerInstanceId && acknowledgement.peerInstanceId !== session.peerInstanceId) {
            return;
        }

        const timeout = session.pendingInputAcks.get(acknowledgement.inputEventId);
        if (timeout) {
            clearTimeout(timeout);
            session.pendingInputAcks.delete(acknowledgement.inputEventId);
        }

        console.debug("remote_input_ack_received", {
            remoteSupportSessionId: session.sessionId,
            targetWindowsSessionId: session.targetWindowsSessionId,
            peerInstanceId: session.peerInstanceId,
            inputEventId: acknowledgement.inputEventId,
            inputAcknowledgedAt: acknowledgement.inputAcknowledgedAt,
            browserAcknowledgementReceivedAt: new Date().toISOString(),
            accepted: acknowledgement.accepted,
            result: acknowledgement.injectionResultCount,
            win32Error: acknowledgement.win32Error,
            status: acknowledgement.status
        });
        session.dotNetRef?.invokeMethodAsync(
            "OnControlInputAcknowledged",
            session.generation || 1,
            acknowledgement);
    }

    function sendInputProbe(sessionId) {
        const session = sessions.get(sessionId);
        if (!session || !session.controlEnabled) {
            return false;
        }

        return sendControl(session, {
            type: "mouse_move",
            x: 0.5,
            y: 0.5,
            inputKind: "mouse",
            keyCategory: "mouse_move",
            diagnosticProbe: true
        });
    }

    function categorizeKey(ev) {
        const key = ev.key || "";
        if (key.length === 1) {
            if (/^[a-z]$/i.test(key)) return "letter";
            if (/^[0-9]$/.test(key)) return "digit";
            if (/^\s$/.test(key)) return "punctuation";
            return "punctuation";
        }

        if (ev.ctrlKey || ev.shiftKey || ev.altKey || ev.metaKey || ["Shift", "Control", "Alt", "Meta"].includes(key)) {
            return "modifier";
        }

        if ((ev.code || "").startsWith("Numpad")) {
            return "digit";
        }

        if (["Enter", "Escape", "Backspace", "Tab", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Delete", "Home", "End", "PageUp", "PageDown", "Insert"].includes(key)) {
            return "navigation";
        }

        return "navigation";
    }

    function categorizeInputType(type) {
        if (type === "mouse_move") return "mouse_move";
        if (type === "mouse_wheel") return "mouse_wheel";
        if (type === "mouse_down" || type === "mouse_up") return "mouse_button";
        return "navigation";
    }

    function relativeVideoPoint(video, ev) {
        const rect = video.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) {
            return null;
        }

        const intrinsicWidth = video.videoWidth || 16;
        const intrinsicHeight = video.videoHeight || 9;
        const scale = Math.min(rect.width / intrinsicWidth, rect.height / intrinsicHeight);
        const drawnWidth = intrinsicWidth * scale;
        const drawnHeight = intrinsicHeight * scale;
        const offsetX = (rect.width - drawnWidth) / 2;
        const offsetY = (rect.height - drawnHeight) / 2;
        const x = ev.clientX - rect.left - offsetX;
        const y = ev.clientY - rect.top - offsetY;

        if (x < 0 || y < 0 || x > drawnWidth || y > drawnHeight) {
            return null;
        }

        return {
            x: Math.max(0, Math.min(1, x / drawnWidth)),
            y: Math.max(0, Math.min(1, y / drawnHeight))
        };
    }

    async function applyRemoteDescription(sessionId, providerGeneration, descriptionJson) {
        const session = sessions.get(sessionId);
        if (!session) {
            throw new Error("Remote support peer connection was not found.");
        }

        if (Number(providerGeneration || 1) !== Number(session.generation || 1)) {
            return;
        }

        const normalized = normalizeRemoteSupportSignalPayload("answer", providerGeneration, descriptionJson);
        debugSignalShape("answer", normalized);
        if (!isValidSessionDescription(normalized.payload)) {
            await reportSignalPayloadError(session, "remote_support_invalid_sdp_payload_shape", "answer", normalized);
            return;
        }

        await session.pc.setRemoteDescription(normalized.payload);
        while (session.pendingCandidates.length > 0) {
            const candidate = session.pendingCandidates.shift();
            try {
                await session.pc.addIceCandidate(candidate);
            } catch (error) {
                console.warn("Remote support queued ICE candidate failed.", error);
            }
        }
    }

    async function addIceCandidate(sessionId, providerGeneration, candidateJson) {
        const session = sessions.get(sessionId);
        if (!session) {
            return;
        }

        if (Number(providerGeneration || 1) !== Number(session.generation || 1)) {
            return;
        }

        const normalized = normalizeRemoteSupportSignalPayload("ice", providerGeneration, candidateJson);
        debugSignalShape("ice", normalized);
        if (!isValidIceCandidate(normalized.payload)) {
            await reportSignalPayloadError(session, "remote_support_invalid_ice_payload_shape", "ice", normalized);
            return;
        }

        const candidate = normalized.payload;
        if (!session.pc.remoteDescription) {
            session.pendingCandidates.push(candidate);
            return;
        }

        await session.pc.addIceCandidate(candidate);
    }

    async function close(sessionId) {
        await closePeer(sessionId);
        sessions.delete(sessionId);
    }

    async function resetTransition(sessionId, providerGeneration) {
        const session = sessions.get(sessionId);
        if (!session) {
            return;
        }

        session.generation = Number(providerGeneration || (session.generation || 1) + 1);
        session.controlEnabled = false;
        session.pendingCandidates = [];
        session.pendingMouseMove = null;
        if (session.mouseMoveTimer) {
            clearTimeout(session.mouseMoveTimer);
            session.mouseMoveTimer = null;
        }
        await closePeer(sessionId);
    }

    async function closePeer(sessionId) {
        const session = sessions.get(sessionId);
        if (!session) {
            return;
        }

        try { session.channel?.close(); } catch { }
        for (const timeout of session.pendingInputAcks?.values?.() || []) {
            clearTimeout(timeout);
        }
        session.pendingInputAcks?.clear?.();
        session.channel = null;
        try { session.pc?.close(); } catch { }
        if (session.statsTimer) {
            clearInterval(session.statsTimer);
        }
        if (session.frameFallbackTimer) {
            clearInterval(session.frameFallbackTimer);
        }
        if (session.video && session.frameFallbackHandler) {
            session.video.removeEventListener("loadeddata", session.frameFallbackHandler);
            session.video.removeEventListener("playing", session.frameFallbackHandler);
        }
        detachPointerHandlers(session);
        detachKeyboardHandlers(session);
        if (session.video) {
            try { session.video.srcObject = null; } catch { }
        }
    }

    function normalizeRemoteSupportSignalPayload(signalType, providerGeneration, payloadValue) {
        let topLevel = parseMaybeJson(payloadValue);
        let payload = topLevel;
        let payloadShape = isObject(topLevel) ? "raw_object" : typeof topLevel;
        let generation = Number(providerGeneration || 1);
        let provider = null;
        let handoverReason = null;

        if (isObject(topLevel) && Object.prototype.hasOwnProperty.call(topLevel, "providerGeneration")) {
            generation = Number(topLevel.providerGeneration || generation || 1);
            provider = topLevel.provider || null;
            handoverReason = topLevel.handoverReason || null;
            if (Object.prototype.hasOwnProperty.call(topLevel, "payload")) {
                payloadShape = typeof topLevel.payload === "string" ? "wrapped_payload_string" : "wrapped_payload_object";
                payload = parseMaybeJson(topLevel.payload);
            } else if (Object.prototype.hasOwnProperty.call(topLevel, "payloadJson")) {
                payloadShape = "wrapped_payloadJson_string";
                payload = parseMaybeJson(topLevel.payloadJson);
            } else {
                payloadShape = "wrapped_missing_payload";
                payload = {};
            }
        }

        return {
            providerGeneration: Number.isFinite(generation) && generation > 0 ? generation : 1,
            payload,
            payloadShape,
            provider,
            handoverReason,
            signalType,
            hasType: isObject(payload) && typeof payload.type === "string",
            hasSdp: isObject(payload) && typeof payload.sdp === "string",
            sdpLength: isObject(payload) && typeof payload.sdp === "string" ? payload.sdp.length : 0,
            topLevelKeys: keysOf(topLevel),
            payloadKeys: keysOf(payload)
        };
    }

    function parseMaybeJson(value) {
        if (typeof value !== "string") {
            return value;
        }

        try {
            return JSON.parse(value);
        } catch {
            return value;
        }
    }

    function isObject(value) {
        return value !== null && typeof value === "object" && !Array.isArray(value);
    }

    function keysOf(value) {
        return isObject(value) ? Object.keys(value) : [];
    }

    function isValidSessionDescription(description) {
        return isObject(description) &&
            (description.type === "answer" || description.type === "offer") &&
            typeof description.sdp === "string";
    }

    function isValidIceCandidate(candidate) {
        return isObject(candidate) &&
            typeof candidate.candidate === "string" &&
            candidate.candidate.length > 0;
    }

    async function reportSignalPayloadError(session, code, signalType, normalized) {
        const diagnostics = {
            code,
            signalType,
            providerGeneration: normalized.providerGeneration,
            payloadShape: normalized.payloadShape,
            hasType: !!normalized.hasType,
            hasSdp: !!normalized.hasSdp,
            sdpLength: normalized.sdpLength || 0,
            topLevelKeys: normalized.topLevelKeys || [],
            payloadKeys: normalized.payloadKeys || [],
            provider: normalized.provider || null,
            handoverReason: normalized.handoverReason || null
        };

        try {
            await session.dotNetRef?.invokeMethodAsync(
                "OnSignalPayloadError",
                normalized.providerGeneration,
                code,
                JSON.stringify(diagnostics));
        } catch {
            try {
                await session.dotNetRef?.invokeMethodAsync("OnControlInputError", normalized.providerGeneration, code);
            } catch { }
        }
    }

    function debugSignalShape(signalType, normalized) {
        if (window.netratelRemoteSupportDebug === true) {
            console.info("[RemoteSupport] signal payload", {
                signalType,
                providerGeneration: normalized.providerGeneration,
                payloadShape: normalized.payloadShape,
                topLevelKeys: normalized.topLevelKeys,
                payloadKeys: normalized.payloadKeys,
                hasType: normalized.hasType,
                hasSdp: normalized.hasSdp,
                sdpLength: normalized.sdpLength
            });
        }
    }

    function normalizeQualityProfile(profile) {
        const value = typeof profile === "string" ? { profile } : (profile || {});
        const key = String(value.profile || value.name || "low").toLowerCase();
        const presets = {
            low: { profile: "low", maxWidth: 1024, maxHeight: 576, fps: 4, targetKbps: 900 },
            balanced: { profile: "balanced", maxWidth: 1280, maxHeight: 720, fps: 6, targetKbps: 1600 },
            high: { profile: "high", maxWidth: 1600, maxHeight: 900, fps: 8, targetKbps: 2500 },
            ultra: { profile: "ultra", maxWidth: 1920, maxHeight: 1080, fps: 10, targetKbps: 4000 }
        };

        return presets[key] || presets.low;
    }

    function startVideoFrameStats(session) {
        if (!session.video || session.frameStatsStarted) {
            return;
        }

        session.frameStatsStarted = true;
        const recordRenderedFrame = () => {
            if (sessions.get(session.sessionId) !== session) {
                return false;
            }

            session.renderedFrames++;
            session.lastFrameRenderedAt = new Date().toISOString();
            if (!session.firstFrameReported) {
                session.firstFrameReported = true;
                session.dotNetRef.invokeMethodAsync(
                    "OnFirstVideoFrame",
                    session.generation || 1,
                    session.peerInstanceId,
                    session.lastFrameRenderedAt).catch(() => { });
            }
            return true;
        };

        const tick = () => {
            if (!recordRenderedFrame()) {
                return;
            }
            const now = performance.now();
            const elapsed = now - session.lastFrameStatsAt;
            if (elapsed >= 1000) {
                session.renderedFps = Math.round((session.renderedFrames * 1000 / elapsed) * 10) / 10;
                session.renderedFrames = 0;
                session.lastFrameStatsAt = now;
            }

            if (session.video?.requestVideoFrameCallback && sessionsHas(session)) {
                session.video.requestVideoFrameCallback(tick);
            }
        };

        if (session.video.requestVideoFrameCallback) {
            session.video.requestVideoFrameCallback(tick);
            return;
        }

        // Older/embedded browsers may not implement requestVideoFrameCallback. A playing
        // video with current frame data and non-zero intrinsic dimensions is the closest
        // browser-native proof that the current peer produced a decodable frame.
        const observePlayableFrame = () => {
            if (sessions.get(session.sessionId) !== session || session.firstFrameReported) {
                return;
            }
            if (session.video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA &&
                session.video.videoWidth > 0 &&
                session.video.videoHeight > 0) {
                recordRenderedFrame();
            }
        };
        session.frameFallbackHandler = observePlayableFrame;
        session.video.addEventListener("loadeddata", observePlayableFrame);
        session.video.addEventListener("playing", observePlayableFrame);
        session.frameFallbackTimer = setInterval(observePlayableFrame, 250);
        observePlayableFrame();
    }

    function sessionsHas(session) {
        for (const candidate of sessions.values()) {
            if (candidate === session) {
                return true;
            }
        }

        return false;
    }

    function startStatsTimer(sessionId, session) {
        if (session.statsTimer) {
            return;
        }

        session.statsTimer = setInterval(async () => {
            try {
                const stats = await collectStats(session);
                await session.dotNetRef.invokeMethodAsync("OnMediaStatsChanged", session.generation || 1, stats);
            } catch {
            }
        }, 1000);
    }

    async function collectStats(session) {
        let inboundBytes = null;
        let inboundKbps = session.inboundKbps;
        try {
            const report = await session.pc.getStats();
            report.forEach(item => {
                if (item.type === "inbound-rtp" && (item.kind === "video" || item.mediaType === "video")) {
                    inboundBytes = item.bytesReceived ?? inboundBytes;
                    const timestamp = item.timestamp ?? performance.now();
                    if (typeof inboundBytes === "number" &&
                        typeof session.lastInboundBytes === "number" &&
                        typeof session.lastInboundTimestamp === "number" &&
                        timestamp > session.lastInboundTimestamp) {
                        inboundKbps = Math.max(0, Math.round(((inboundBytes - session.lastInboundBytes) * 8) / (timestamp - session.lastInboundTimestamp)));
                    }

                    if (typeof inboundBytes === "number") {
                        session.lastInboundBytes = inboundBytes;
                        session.lastInboundTimestamp = timestamp;
                        session.inboundKbps = inboundKbps;
                    }
                }
            });
        } catch {
        }

        return {
            profile: session.profile || "low",
            videoWidth: session.video?.videoWidth || 0,
            videoHeight: session.video?.videoHeight || 0,
            renderedFps: session.renderedFps || 0,
            webLastFrameRenderedAt: session.lastFrameRenderedAt,
            inboundKbps: inboundKbps ?? null,
            inboundBytes,
            peerState: session.pc?.connectionState || "unknown",
            iceState: session.pc?.iceConnectionState || "unknown",
            dataChannelState: session.channel?.readyState || "unknown"
        };
    }

    function normalizeIceServers(iceServers) {
        if (!Array.isArray(iceServers) || iceServers.length === 0) {
            return [];
        }

        return iceServers
            .map(server => {
                const urls = Array.isArray(server.urls)
                    ? server.urls.filter(url => typeof url === "string" && url.trim().length > 0)
                    : (typeof server.urls === "string" && server.urls.trim().length > 0 ? server.urls : null);
                if (!urls || (Array.isArray(urls) && urls.length === 0)) {
                    return null;
                }

                const normalized = { urls };
                if (server.username) {
                    normalized.username = server.username;
                }
                if (server.credential) {
                    normalized.credential = server.credential;
                }

                return normalized;
            })
            .filter(Boolean);
    }

    return {
        protocolRevision,
        capabilities,
        create,
        applyRemoteDescription,
        addIceCandidate,
        setControlEnabled,
        setQualityProfile,
        refreshVideo,
        sendInputProbe,
        sendSas,
        resetTransition,
        close,
        normalizeRemoteSupportSignalPayload
    };
})();
