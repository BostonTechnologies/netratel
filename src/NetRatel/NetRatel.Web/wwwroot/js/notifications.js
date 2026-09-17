window.scrollToNotificationRow = function (id) {
    const target = document.querySelector(`[data-notification-id="${id}"]`);
    if (!target) {
        return;
    }

    target.scrollIntoView({ behavior: "smooth", block: "center" });
};
