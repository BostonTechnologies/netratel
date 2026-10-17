using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace NetRatel.AgentClient;

public sealed record HttpHostingWebBackendSelection(Dictionary<string, string> Changes, string ApiBackend, string McpBackend);

/// <summary>Merges reviewed HTTP MCP settings into a deployment manager's complete document.</summary>
public static class HttpHostingComposeDocument
{
    public const int MaximumSourceBytes = 262_144;

    /// <summary>Retain deployment-owned backend bindings when the wizard is using its defaults.</summary>
    public static HttpHostingWebBackendSelection SelectWebBackends(string source, IReadOnlyDictionary<string, string> proposed,
        bool preserveApi, bool preserveMcp, string? observedApi = null, string? observedMcp = null, bool environmentObserved = false,
        HttpHostingEffectiveDeployment? effectiveDeployment = null)
    {
        try
        {
            ValidateSyntax(source);
            var yaml = new YamlStream();
            yaml.Load(new StringReader(source));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
                throw Invalid("Use one complete Compose document.");
            var count = 0;
            Validate(root, 0, ref count);
            var service = Map(Map(root, "services", create: false), "web", create: false);
            var changes = proposed.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var envFile = service.Children.TryGetValue(Key("env_file"), out var file) && file is not YamlSequenceNode { Children.Count: 0 } && file is not YamlScalarNode { Value: null or "" };
            string Select(string key, bool preserve, string? observed, string label)
            {
                if (!changes.TryGetValue(key, out var suggested)) throw Invalid($"The reviewed {label} backend is missing.");
                if (!preserve) return Backend(suggested, label);
                var (present, configured) = SourceEnvironment(service, key, effectiveDeployment);
                if (configured is { Length: > 0 } && !configured.Contains('$'))
                {
                    changes.Remove(key);
                    return Backend(configured, label);
                }
                if (!string.IsNullOrWhiteSpace(observed))
                {
                    changes.Remove(key);
                    return Backend(observed, label);
                }
                // A complete selected Web environment establishes that an optional
                // MCP backend is absent. Enabling that new route may then add its
                // reviewed default without overwriting an env_file binding.
                if (key == "WebGateway__Mcp__BackendUrl" && environmentObserved && !present && observed is null)
                    return Backend(suggested, label);
                if (envFile || present)
                    throw Invalid($"The existing Web {label} backend comes from a managed or unresolved source. Start with setup-http-mcp.sh while the Web service is running, or enter its actual private {label} backend URL in Advanced. The wizard default was not applied over that source.");
                return Backend(suggested, label);
            }
            var api = Select("ApiBaseUrl", preserveApi, observedApi, "API");
            var mcp = Select("WebGateway__Mcp__BackendUrl", preserveMcp, observedMcp, "MCP");
            return new(changes, api, mcp);
        }
        catch (Exception exception) when (exception is YamlException or ArgumentException)
        {
            throw Invalid("The source is not supported unambiguous YAML. Check it in the deployment manager without sharing secret values.");
        }
    }

    private static (bool Present, string? Value) SourceEnvironment(YamlMappingNode service, string key, HttpHostingEffectiveDeployment? effectiveDeployment)
    {
        Environment(service, new Dictionary<string, string>(), "web", effectiveDeployment);
        if (!service.Children.TryGetValue(Key("environment"), out var environment)) return (false, null);
        var matches = new List<string?>();
        if (environment is YamlMappingNode mapping)
        {
            foreach (var (name, value) in mapping.Children)
                if (name is YamlScalarNode scalar && string.Equals(scalar.Value?.Replace(":", "__", StringComparison.Ordinal), key, StringComparison.OrdinalIgnoreCase))
                {
                    if (value is not YamlScalarNode selected) throw Invalid("Web backend environment values must be plain URL values.");
                    matches.Add(selected.Value);
                }
        }
        else if (environment is YamlSequenceNode sequence)
            foreach (var entry in sequence.Children)
            {
                if (entry is not YamlScalarNode { Value: not null } scalar) throw Invalid("Environment lists must contain named values.");
                var equals = scalar.Value.IndexOf('=');
                var name = equals < 0 ? scalar.Value : scalar.Value[..equals];
                if (string.Equals(name.Replace(":", "__", StringComparison.Ordinal), key, StringComparison.OrdinalIgnoreCase))
                    matches.Add(equals < 0 ? null : scalar.Value[(equals + 1)..]);
            }
        else throw Invalid("Web environment must be a mapping or a list.");
        if (matches.Count > 1 && matches.Any(value => !string.Equals(value, matches[0], StringComparison.Ordinal)))
            throw Invalid("Conflicting Web backend settings need an explicit single source.");
        return (matches.Count > 0, matches.FirstOrDefault());
    }

    private static string Backend(string value, string label)
    {
        if (value.Length > 2048 || value.Any(char.IsControl) || value.Contains('$') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw Invalid($"The existing Web {label} backend must be an absolute HTTP or HTTPS URL without credentials, query or fragment. Review its private backend in Advanced.");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    public static string Merge(string source, IReadOnlyDictionary<string, string> web,
        IReadOnlyDictionary<string, string> api, IReadOnlyDictionary<string, string> mcp,
        uint runtimeGroup, string setupVolumeName, string clientVolumeName,
        int listeningPort, string? projectName = null, IReadOnlyDictionary<string, string>? resolvedVolumeNames = null,
        HttpHostingEffectiveDeployment? effectiveDeployment = null)
    {
        try
        {
            ValidateSyntax(source);
            var yaml = new YamlStream();
            yaml.Load(new StringReader(source));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
                throw Invalid("Use one complete Compose document.");
            var count = 0;
            Validate(root, 0, ref count);
            if (root.Children.ContainsKey(Key("include")))
                throw Invalid("Compose include needs a complete source document before setup can merge it.");
            var services = Map(root, "services", create: false);
            var volumes = Map(root, "volumes", create: true);
            var webService = Map(services, "web", create: false);
            var apiService = Map(services, "api", create: false);
            var mcpService = Map(services, "mcp-http", create: false);
            foreach (var service in new[] { webService, apiService, mcpService })
                if (service.Children.ContainsKey(Key("extends")))
                    throw Invalid("Inherited services need a complete source document before setup can merge them.");

            var setupKey = VolumeKey(volumes, mcpService, "/setup-output", "http-host-setup", setupVolumeName, projectName, resolvedVolumeNames);
            var clientKey = VolumeKey(volumes, mcpService, "/var/lib/netratel/clients", "http-client-data", clientVolumeName, projectName, resolvedVolumeNames);
            Environment(webService, web, "web", effectiveDeployment);
            Environment(apiService, api, "api", effectiveDeployment);
            Environment(mcpService, mcp, "mcp-http", effectiveDeployment);
            AddGroup(apiService, runtimeGroup);
            AddGroup(mcpService, runtimeGroup);
            AddMount(apiService, "volume", setupKey, "/run/netratel-http-host", readOnly: true);
            AddMount(mcpService, "volume", setupKey, "/run/netratel-http-host", readOnly: true);
            AddMount(mcpService, "volume", setupKey, "/setup-output", readOnly: false);
            AddMount(mcpService, "volume", clientKey, "/var/lib/netratel/clients", readOnly: false);
            // Preserve a deployment's own health check. The generated default is
            // needed only when no check was supplied by its owner.
            if (!mcpService.Children.ContainsKey(Key("healthcheck")))
                mcpService.Add(Key("healthcheck"), new YamlMappingNode
                {
                    { Key("test"), new YamlSequenceNode(Text("CMD"), Text("curl"), Text("--fail"), Text("--silent"), Text($"http://127.0.0.1:{listeningPort}/health/live")) }
                });
            SortMappings(root);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            writer.WriteLine("# Complete reviewed HTTP MCP deployment. Import this entire document into the existing Komodo stack.");
            yaml.Save(writer, assignAnchors: false);
            var result = writer.ToString();
            if (System.Text.Encoding.UTF8.GetByteCount(result) > MaximumSourceBytes)
                throw Invalid("The merged document is too large for protected setup storage.");
            return result;
        }
        catch (Exception exception) when (exception is YamlException or ArgumentException)
        {
            // Parser messages can contain source lines, including an owner's
            // original inline secrets. Keep those messages out of the terminal.
            throw Invalid("The source is not supported unambiguous YAML. Check it in the deployment manager without sharing secret values.");
        }
    }

    private static void SortMappings(YamlNode node)
    {
        if (node is YamlMappingNode map)
        {
            var ordered = map.Children.OrderBy(pair => ((YamlScalarNode)pair.Key).Value, StringComparer.Ordinal).ToArray();
            map.Children.Clear();
            foreach (var (key, value) in ordered)
            {
                SortMappings(value);
                map.Add(key, value);
            }
        }
        else if (node is YamlSequenceNode sequence)
            foreach (var child in sequence.Children) SortMappings(child);
    }

    private static void ValidateSyntax(string source)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes)
            throw Invalid("The source exceeds the supported 256 KiB protected document size.");
        var parser = new Parser(new StringReader(source));
        var depth = 0;
        var count = 0;
        while (parser.MoveNext())
        {
            if (++count > 16_384 || parser.Current is YamlDotNet.Core.Events.AnchorAlias)
                throw Invalid("Aliases and excessively complex YAML need a complete plain source document before setup can merge it.");
            if (parser.Current is YamlDotNet.Core.Events.MappingStart or YamlDotNet.Core.Events.SequenceStart)
            {
                if (++depth > 48) throw Invalid("The source nesting exceeds the supported document depth.");
            }
            else if (parser.Current is YamlDotNet.Core.Events.MappingEnd or YamlDotNet.Core.Events.SequenceEnd) depth--;
        }
    }

    private static void Validate(YamlNode node, int depth, ref int count)
    {
        if (++count > 8192 || depth > 48 || !node.Anchor.IsEmpty ||
            !node.Tag.IsEmpty && !node.Tag.IsNonSpecific && !node.Tag.Value.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal))
            throw Invalid("Anchors, aliases, custom tags and excessively complex YAML need a complete plain source document before setup can merge it.");
        if (node is YamlMappingNode map)
            foreach (var (key, value) in map.Children)
            {
                if (key is not YamlScalarNode { Value: not null } scalar || scalar.Value == "<<")
                    throw Invalid("Mapping keys must be explicit names; YAML merge keys are not supported.");
                Validate(key, depth + 1, ref count);
                Validate(value, depth + 1, ref count);
            }
        else if (node is YamlSequenceNode sequence)
            foreach (var child in sequence.Children) Validate(child, depth + 1, ref count);
    }

    private static YamlMappingNode Map(YamlMappingNode parent, string key, bool create)
    {
        if (parent.Children.TryGetValue(Key(key), out var existing))
        {
            if (existing is YamlMappingNode mapping) return mapping;
            // Compose permits an empty volume declaration.
            if (create && existing is YamlScalarNode { Value: null or "" })
            {
                var empty = new YamlMappingNode(); parent.Children[Key(key)] = empty; return empty;
            }
            throw Invalid($"The {key} entry must be a mapping.");
        }
        if (!create) throw Invalid($"The actual Compose document must contain the {key} service or section.");
        var result = new YamlMappingNode(); parent.Add(Key(key), result); return result;
    }

    private static void Environment(YamlMappingNode service, IReadOnlyDictionary<string, string> changes, string serviceName,
        HttpHostingEffectiveDeployment? effectiveDeployment)
    {
        var environment = new YamlMappingNode();
        var seen = new Dictionary<string, (string Name, string? Value, int Index)>(HttpHostingEnvironmentNames.Comparer);
        var index = 0;
        void Add(string name, YamlScalarNode value)
        {
            index++;
            var key = HttpHostingEnvironmentNames.Canonical(name);
            var selected = (value.Style is ScalarStyle.Plain or ScalarStyle.Any) && (value.Value is null or "" or "~" or "null" or "Null" or "NULL") ? null : value.Value;
            if (seen.TryGetValue(key, out var previous))
            {
                if (HttpHostingEnvironmentNames.Recognized(key) &&
                    (string.Equals(previous.Value, selected, StringComparison.Ordinal) ||
                     effectiveDeployment?.EqualComposeAliases(serviceName, previous.Name, name) == true)) return;
                throw HttpHostingEnvironmentNames.Conflict(serviceName, key, previous.Name, name, "Compose environment", previous.Index, index);
            }
            seen.Add(key, (name, selected, index));
            environment.Add(Key(key), value);
        }
        if (!service.Children.TryGetValue(Key("environment"), out var existing)) { }
        else if (existing is YamlMappingNode mapping)
        {
            foreach (var (name, value) in mapping.Children)
            {
                if (name is not YamlScalarNode { Value: not null } key || value is not YamlScalarNode scalar) throw Invalid("Environment names and values must be scalar settings.");
                Add(key.Value, scalar);
            }
        }
        else if (existing is YamlSequenceNode sequence)
        {
            foreach (var entry in sequence.Children)
            {
                if (entry is not YamlScalarNode { Value: not null } scalar) throw Invalid("Environment lists must contain named values.");
                var equals = scalar.Value.IndexOf('=');
                var key = equals < 0 ? scalar.Value : scalar.Value[..equals];
                Add(key, equals < 0 ? new YamlScalarNode((string?)null) : Text(scalar.Value[(equals + 1)..]));
            }
        }
        else throw Invalid("Environment must be a mapping or a list.");
        foreach (var (key, value) in changes)
        {
            var previous = environment.Children.Keys.OfType<YamlScalarNode>().SingleOrDefault(item => HttpHostingEnvironmentNames.Comparer.Equals(item.Value, key));
            if (previous is not null) environment.Children.Remove(previous);
            environment.Add(Key(HttpHostingEnvironmentNames.Canonical(key)), Text(value.Replace("$", "$$", StringComparison.Ordinal)));
        }
        service.Children[Key("environment")] = environment;
    }

    private static string VolumeKey(YamlMappingNode volumes, YamlMappingNode mcp, string target, string preferred, string physical, string? projectName, IReadOnlyDictionary<string, string>? resolvedVolumeNames)
    {
        var current = Mounts(mcp).SingleOrDefault(mount => mount.Target == target);
        if (current is not null)
        {
            if (current.Type != "volume" || string.IsNullOrEmpty(current.Source))
                throw Invalid($"The existing {target} mount must be the persistent named volume selected by the launcher.");
            if (!volumes.Children.TryGetValue(Key(current.Source), out var definition))
                throw Invalid($"The existing {target} volume needs an explicit top-level declaration.");
            string? expected = null;
            if (definition is YamlMappingNode details)
            {
                expected = Value(details, "name");
                if (expected?.Contains('$') == true && resolvedVolumeNames?.TryGetValue(current.Source, out expected) != true)
                    throw Invalid($"The existing {target} volume name uses unresolved interpolation. Setup cannot prove it matches the selected persistent volume.");
                if (expected is null && bool.TryParse(Value(details, "external"), out var external) && external) expected = current.Source;
            }
            else if (definition is not YamlScalarNode { Value: null or "" })
                throw Invalid($"The existing {target} volume declaration must be an explicit mapping.");
            expected ??= resolvedVolumeNames?.GetValueOrDefault(current.Source) ?? (projectName is null ? null : projectName + "_" + current.Source);
            if (expected is null || expected != physical)
                throw Invalid($"The {target} volume does not match the launcher's selected persistent volume. Reopen setup through setup-http-mcp.sh.");
            return current.Source;
        }
        foreach (var (name, definition) in volumes.Children)
            if (name is YamlScalarNode { Value: not null } key && definition is YamlMappingNode details &&
                details.Children.TryGetValue(Key("name"), out var value) && value is YamlScalarNode scalar && scalar.Value == physical)
                return key.Value;
        var chosen = preferred;
        if (volumes.Children.ContainsKey(Key(chosen))) chosen = "netratel-" + preferred;
        if (volumes.Children.ContainsKey(Key(chosen))) throw Invalid("Persistent setup volume names conflict with existing volume declarations.");
        volumes.Add(Key(chosen), new YamlMappingNode { { Key("external"), new YamlScalarNode("true") }, { Key("name"), Text(physical) } });
        return chosen;
    }

    private static void AddGroup(YamlMappingNode service, uint group)
    {
        var text = group.ToString(CultureInfo.InvariantCulture);
        var groups = Sequence(service, "group_add");
        if (!groups.Children.OfType<YamlScalarNode>().Any(item => item.Value == text)) groups.Add(Text(text));
    }

    private static void AddMount(YamlMappingNode service, string type, string source, string target, bool readOnly)
    {
        var mounts = Mounts(service);
        if (mounts.GroupBy(item => item.Target, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw Invalid("Duplicate volume targets need an explicit single source.");
        var current = mounts.SingleOrDefault(item => item.Target == target);
        if (current is not null)
        {
            if (current.Type != type || current.Source != source || current.ReadOnly != readOnly)
                throw Invalid($"The existing {target} mount conflicts with the reviewed setup mount. Keep the source intact and select the matching persistent deployment.");
            return;
        }
        var mount = new YamlMappingNode
        {
            { Key("type"), Key(type) }, { Key("source"), Text(source.Replace("$", "$$", StringComparison.Ordinal)) }, { Key("target"), Text(target) }
        };
        if (readOnly) mount.Add(Key("read_only"), new YamlScalarNode("true"));
        if (type == "bind") mount.Add(Key("bind"), new YamlMappingNode { { Key("create_host_path"), new YamlScalarNode("false") } });
        Sequence(service, "volumes").Add(mount);
    }

    private sealed record Mount(string Type, string Source, string Target, bool ReadOnly);
    private static List<Mount> Mounts(YamlMappingNode service)
    {
        if (!service.Children.ContainsKey(Key("volumes"))) return [];
        var sequence = Sequence(service, "volumes");
        return sequence.Children.Select(node =>
        {
            if (node is YamlScalarNode { Value: not null } scalar)
            {
                var parts = scalar.Value.Split(':');
                if (parts.Length == 1 && parts[0].StartsWith('/')) return new Mount("anonymous", "", parts[0], false);
                if (parts.Length is < 2 or > 3) throw Invalid("Use explicit named or bind volume mounts for this Linux deployment.");
                return new Mount(parts[0].StartsWith('/') || parts[0].StartsWith('.') ? "bind" : "volume", parts[0], parts[1], parts.Length == 3 && parts[2].Split(',').Contains("ro", StringComparer.Ordinal));
            }
            if (node is not YamlMappingNode map || Value(map, "target") is not { } target)
                throw Invalid("Volume mounts need explicit sources and targets.");
            var type = Value(map, "type") ?? "volume";
            var source = Value(map, "source");
            if (source is null && type is not ("volume" or "tmpfs")) throw Invalid("Bind mounts need explicit sources.");
            return new Mount(source is null && type == "volume" ? "anonymous" : type, source ?? "", target,
                bool.TryParse(Value(map, "read_only"), out var readOnly) && readOnly);
        }).ToList();
    }

    private static string? Value(YamlMappingNode map, string key) => map.Children.TryGetValue(Key(key), out var value) && value is YamlScalarNode scalar ? scalar.Value : null;
    private static YamlSequenceNode Sequence(YamlMappingNode parent, string key)
    {
        if (parent.Children.TryGetValue(Key(key), out var existing))
            return existing as YamlSequenceNode ?? throw Invalid($"The {key} entry must be a list.");
        var result = new YamlSequenceNode(); parent.Add(Key(key), result); return result;
    }
    private static YamlScalarNode Key(string value) => new(value);
    private static YamlScalarNode Text(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };
    private static AgentClientValidationException Invalid(string detail) => new("Komodo Compose preflight: " + detail + " No existing file was changed.");
}
