(() => {
    const setText = (id, value, fallback = "") => {
        const element = document.getElementById(id);
        if (!element) {
            return;
        }

        element.textContent = value ?? fallback;
    };

    const setBadgeClass = (id, badgeClass) => {
        const element = document.getElementById(id);
        if (!element) {
            return;
        }

        element.classList.remove("success", "warn", "danger", "info", "neutral");
        if (badgeClass) {
            element.classList.add(badgeClass);
        }
    };

    const formatRelative = isoUtc => {
        if (!isoUtc) return "";
        const then = Date.parse(isoUtc);
        if (Number.isNaN(then)) return "";
        const diffSec = Math.max(0, Math.round((Date.now() - then) / 1000));
        if (diffSec < 5) return "just now";
        if (diffSec < 60) return diffSec + "s ago";
        if (diffSec < 3600) return Math.round(diffSec / 60) + "m ago";
        if (diffSec < 86400) return Math.round(diffSec / 3600) + "h ago";
        return Math.round(diffSec / 86400) + "d ago";
    };

    const refreshRelativeTimes = () => {
        for (const element of document.querySelectorAll("[data-utc]")) {
            const iso = element.getAttribute("data-utc");
            if (!iso) continue;
            element.textContent = formatRelative(iso);
            if (!element.title) {
                element.title = iso;
            }
        }
    };

    const initRelativeTimes = () => {
        refreshRelativeTimes();
        window.setInterval(refreshRelativeTimes, 30000);
    };

    const initAutoRefresh = () => {
        const { body } = document;
        if (!body) {
            return;
        }

        if (body.dataset.autoRefreshEnabled !== "true") {
            return;
        }

        const toggle = document.querySelector("[data-auto-refresh-toggle='true']");
        const intervalSelect = document.querySelector("[data-auto-refresh-interval='true']");
        if (!(toggle instanceof HTMLInputElement) || !(intervalSelect instanceof HTMLSelectElement)) {
            return;
        }

        const enabledStorageKey = "twitchArchivist.autoRefresh.enabled";
        const intervalStorageKey = "twitchArchivist.autoRefresh.intervalSeconds";
        const defaultIntervalSeconds = Number.parseInt(body.dataset.autoRefreshDefaultIntervalSeconds ?? "120", 10);

        const normalizeInterval = value => {
            const parsed = Number.parseInt(value ?? "", 10);
            return Number.isFinite(parsed) && parsed > 0 ? parsed : defaultIntervalSeconds;
        };

        const readEnabled = () => {
            const persisted = localStorage.getItem("twitchArchivist.autoRefresh.enabled");
            return persisted === null ? true : persisted === "true";
        };

        const readIntervalSeconds = () =>
            normalizeInterval(localStorage.getItem("twitchArchivist.autoRefresh.intervalSeconds"));

        const writeEnabled = value => {
            localStorage.setItem(enabledStorageKey, value ? "true" : "false");
        };

        const writeIntervalSeconds = value => {
            localStorage.setItem(intervalStorageKey, String(normalizeInterval(value)));
        };

        const isRefreshPaused = () =>
            document.hidden ||
            document.querySelector("[data-portal='true']:not([hidden])") !== null;

        const applyControlState = () => {
            const enabled = readEnabled();
            const intervalSeconds = readIntervalSeconds();
            toggle.checked = enabled;
            intervalSelect.value = String(intervalSeconds);
        };

        applyControlState();

        toggle.addEventListener("change", () => {
            writeEnabled(toggle.checked);
        });

        intervalSelect.addEventListener("change", () => {
            writeIntervalSeconds(intervalSelect.value);
            intervalSelect.value = String(readIntervalSeconds());
        });

        // Pages with [data-live-panel] panels get in-place refresh: the page is
        // re-fetched and the matching panels swapped in without a full reload.
        // Pages without those panels fall back to window.location.reload().
        const livePanels = () => Array.from(document.querySelectorAll("[data-live-panel]"));
        const liveRefresh = livePanels().length > 0
            ? async () => {
                const response = await fetch(window.location.href, {
                    cache: "no-store",
                    headers: { "X-Requested-With": "live-refresh" }
                });
                if (!response.ok) return;
                const html = await response.text();
                const parser = new DOMParser();
                const doc = parser.parseFromString(html, "text/html");
                for (const oldPanel of livePanels()) {
                    const id = oldPanel.getAttribute("data-live-panel");
                    if (!id) continue;
                    const newPanel = doc.querySelector(`[data-live-panel='${CSS.escape(id)}']`);
                    if (newPanel) {
                        oldPanel.replaceWith(newPanel);
                    }
                }
                initRelativeTimes();
            }
            : null;

        let nextRefreshAt = Date.now() + readIntervalSeconds() * 1000;

        window.setInterval(async () => {
            const enabled = readEnabled();
            const intervalSeconds = readIntervalSeconds();

            if (!enabled) {
                nextRefreshAt = Date.now() + intervalSeconds * 1000;
                return;
            }

            if (Date.now() < nextRefreshAt) {
                return;
            }

            if (isRefreshPaused()) {
                return;
            }

            nextRefreshAt = Date.now() + intervalSeconds * 1000;

            if (liveRefresh) {
                try {
                    await liveRefresh();
                } catch {
                    // Fall back to no-op; next tick will retry.
                }
                return;
            }

            window.location.reload();
        }, 1000);

        window.setInterval(() => {
            applyControlState();
        }, 5000);
    };

    const eventSubBadgeClass = state => {
        switch (state) {
            case "connected":
            case "reconnected":
                return "success";
            case "connecting":
            case "awaiting-authorization":
                return "warn";
            case "disconnected":
            case "error":
                return "danger";
            case "not-configured":
                return "neutral";
            default:
                return "neutral";
        }
    };

    const validityBadgeClass = validity => {
        if (validity === "valid") return "success";
        if (validity === "expiring-soon") return "warn";
        return "danger";
    };

    let lastUpdatedUtc = null;

    const tickRelative = () => {
        const target = document.getElementById("runtime-relative");
        if (!target) return;
        target.textContent = lastUpdatedUtc ? "Updated " + formatRelative(lastUpdatedUtc) : "";
    };

    const renderRuntimeStatus = payload => {
        const diagnosticsMessage = payload.diagnosticsMessage && payload.diagnosticsMessage.trim().length > 0
            ? payload.diagnosticsMessage
            : "—";
        setText("runtime-diagnostics-message", diagnosticsMessage);
        const eventSubState = payload.eventSubConnectionState ?? "";
        setText("runtime-eventsub-state", eventSubState);
        setBadgeClass("runtime-eventsub-state", eventSubBadgeClass(eventSubState));
        setText("runtime-twitch-configured", payload.twitchUserAuthorizationConfigured ? "Configured" : "Missing");
        const validity = payload.twitchUserAuthorizationValidity ?? "missing";
        setText("runtime-twitch-validity", validity);
        setBadgeClass("runtime-twitch-validity", validityBadgeClass(validity));
        setText("runtime-twitch-user", payload.twitchUserAuthorizationLogin ?? "Not authorized");
        setText("runtime-twitch-expires", payload.twitchUserAuthorizationExpiresUtc ?? "Not authorized");
        setText("runtime-twitch-last-validated", payload.twitchUserAuthorizationLastValidatedUtc ?? "Not validated");
        setText("runtime-twitch-detail", payload.twitchUserAuthorizationDetail ?? "");
        setText("runtime-updated-utc", payload.updatedUtc ?? "");
        if (payload.updatedUtc) {
            lastUpdatedUtc = payload.updatedUtc;
            tickRelative();
        }
    };

    const refreshRuntimeStatus = async () => {
        try {
            const response = await fetch("/api/runtime-status", { cache: "no-store" });
            if (!response.ok) {
                return;
            }

            const payload = await response.json();
            renderRuntimeStatus(payload);
        } catch {
        }
    };

    if (document.getElementById("runtime-twitch-validity") || document.getElementById("runtime-eventsub-state")) {
        refreshRuntimeStatus();
        window.setInterval(refreshRuntimeStatus, 15000);
        window.setInterval(tickRelative, 5000);
    }

    // ---------------------------------------------------------------------
    // Filesystem picker (used by directory and executable pickers).
    // ---------------------------------------------------------------------

    const getParentPath = path => {
        if (!path) {
            return "";
        }

        const normalized = path.replace(/[\\/]+$/, "");
        const lastSeparatorIndex = Math.max(normalized.lastIndexOf("\\"), normalized.lastIndexOf("/"));
        if (lastSeparatorIndex < 0) {
            return "";
        }

        const parent = normalized.slice(0, lastSeparatorIndex + 1);
        if (!parent || parent === path) {
            return "";
        }

        return parent;
    };

    const FOCUSABLE_SELECTORS = [
        "a[href]",
        "button:not([disabled])",
        "input:not([disabled])",
        "select:not([disabled])",
        "textarea:not([disabled])",
        "[tabindex]:not([tabindex='-1'])"
    ].join(",");

    const trapFocus = (container, event) => {
        if (event.key !== "Tab") return;
        const focusable = Array.from(container.querySelectorAll(FOCUSABLE_SELECTORS))
            .filter(element => !element.hasAttribute("hidden"));
        if (focusable.length === 0) {
            event.preventDefault();
            return;
        }

        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        const active = document.activeElement;
        if (event.shiftKey) {
            if (active === first || !container.contains(active)) {
                event.preventDefault();
                last.focus();
            }
        } else {
            if (active === last) {
                event.preventDefault();
                first.focus();
            }
        }
    };

    const initPicker = ({ namespace, includeFiles, searchPattern }) => {
        const portal = document.querySelector(`[data-${namespace}-portal='true']`);
        if (!(portal instanceof HTMLDivElement)) {
            return;
        }

        const list = portal.querySelector(`[data-${namespace}-list='true']`);
        const rootsButton = portal.querySelector(`[data-${namespace}-roots='true']`);
        const upButton = portal.querySelector(`[data-${namespace}-up='true']`);
        const closeButton = portal.querySelector(`[data-${namespace}-close='true']`);
        const chooseButton = portal.querySelector(`[data-${namespace}-choose='true']`);
        const currentPathLabel = portal.querySelector(`[data-${namespace}-current-path='true']`);
        const errorLabel = portal.querySelector(`[data-${namespace}-error='true']`);
        const emptyLabel = portal.querySelector(`[data-${namespace}-empty='true']`);
        if (!(list instanceof HTMLUListElement) ||
            !(rootsButton instanceof HTMLButtonElement) ||
            !(upButton instanceof HTMLButtonElement) ||
            !(closeButton instanceof HTMLButtonElement) ||
            !(currentPathLabel instanceof HTMLDivElement) ||
            !(errorLabel instanceof HTMLParagraphElement) ||
            !(emptyLabel instanceof HTMLParagraphElement)) {
            return;
        }

        let activeInput = null;
        let lastFocusedElement = null;
        let currentPath = "";

        const setPortalError = message => {
            if (!message) {
                errorLabel.hidden = true;
                errorLabel.textContent = "";
                return;
            }

            errorLabel.hidden = false;
            errorLabel.textContent = message;
        };

        const setPortalPath = path => {
            currentPath = path ?? "";
            currentPathLabel.textContent = currentPath || "Choose a drive or root folder.";
        };

        const renderList = entries => {
            list.innerHTML = "";
            emptyLabel.hidden = Array.isArray(entries) && entries.length > 0;

            for (const entry of entries ?? []) {
                const item = document.createElement("li");
                const button = document.createElement("button");
                button.type = "button";
                button.className = "portal-row";
                const isDirectory = entry.isDirectory ?? true;
                const labelSpan = document.createElement("span");
                labelSpan.className = "mono";
                labelSpan.textContent = entry.path;
                const actionSpan = document.createElement("span");
                actionSpan.textContent = isDirectory ? "Open" : "Select";
                button.append(labelSpan, actionSpan);
                button.addEventListener("click", async () => {
                    if (isDirectory) {
                        await loadEntries(entry.path);
                        return;
                    }

                    if (activeInput) {
                        activeInput.value = entry.path;
                    }

                    closePortal();
                });
                item.appendChild(button);
                list.appendChild(item);
            }
        };

        const loadRoots = async () => {
            setPortalError("");
            setPortalPath("");
            list.innerHTML = "";
            emptyLabel.hidden = true;

            try {
                const response = await fetch("/api/filesystem/roots", { cache: "no-store" });
                if (!response.ok) {
                    setPortalError("Unable to load filesystem roots.");
                    return;
                }

                const roots = await response.json();
                renderList((roots ?? []).map(entry => ({ ...entry, isDirectory: true })));
            } catch {
                setPortalError("Unable to load filesystem roots.");
            }
        };

        const loadEntries = async path => {
            setPortalError("");
            setPortalPath(path);
            list.innerHTML = "";
            emptyLabel.hidden = true;

            const url = includeFiles
                ? `/api/filesystem/entries?path=${encodeURIComponent(path)}&includeFiles=true&searchPattern=${encodeURIComponent(searchPattern ?? "*")}`
                : `/api/filesystem/directories?path=${encodeURIComponent(path)}`;

            try {
                const response = await fetch(url, { cache: "no-store" });
                if (!response.ok) {
                    const payload = await response.json().catch(() => null);
                    setPortalError(payload?.error ?? "Unable to load filesystem entries.");
                    return;
                }

                const data = await response.json();
                if (includeFiles) {
                    renderList(data);
                } else {
                    renderList((data ?? []).map(entry => ({ ...entry, isDirectory: true })));
                }
            } catch {
                setPortalError("Unable to load filesystem entries.");
            }
        };

        const closePortal = () => {
            portal.hidden = true;
            setPortalError("");
            document.removeEventListener("keydown", onKeydown);
            if (lastFocusedElement instanceof HTMLElement) {
                lastFocusedElement.focus();
            }
            activeInput = null;
            lastFocusedElement = null;
        };

        const onKeydown = event => {
            if (portal.hidden) return;
            if (event.key === "Escape") {
                event.preventDefault();
                closePortal();
                return;
            }
            trapFocus(portal, event);
        };

        document.querySelectorAll(`[data-${namespace}='true']`).forEach(input => {
            if (!(input instanceof HTMLInputElement)) {
                return;
            }

            const button = input.parentElement?.querySelector(`[data-${namespace}-button='true']`);
            if (!(button instanceof HTMLButtonElement)) {
                return;
            }

            button.addEventListener("click", async () => {
                activeInput = input;
                lastFocusedElement = button;
                portal.hidden = false;
                document.addEventListener("keydown", onKeydown);

                const initialPath = input.value.trim();
                if (initialPath) {
                    if (includeFiles) {
                        const parentPath = getParentPath(initialPath);
                        await loadEntries(parentPath || initialPath);
                    } else {
                        await loadEntries(initialPath);
                    }
                } else {
                    await loadRoots();
                }

                closeButton.focus();
            });
        });

        rootsButton.addEventListener("click", loadRoots);
        upButton.addEventListener("click", async () => {
            const parentPath = getParentPath(currentPath);
            if (parentPath) {
                await loadEntries(parentPath);
                return;
            }

            await loadRoots();
        });
        if (chooseButton instanceof HTMLButtonElement) {
            chooseButton.addEventListener("click", () => {
                if (activeInput && currentPath) {
                    activeInput.value = currentPath;
                }

                closePortal();
            });
        }
        closeButton.addEventListener("click", closePortal);
        portal.addEventListener("click", event => {
            if (event.target === portal) {
                closePortal();
            }
        });
    };

    const initTwitchLoginAutocomplete = () => {
        document.querySelectorAll("[data-twitch-login-autocomplete='true']").forEach(input => {
            if (!(input instanceof HTMLInputElement)) {
                return;
            }

            const minLength = Number.parseInt(input.dataset.twitchLoginMinLength ?? "4", 10);
            const wrap = input.closest("[data-twitch-login-wrap='true']");
            const suggestions = wrap?.querySelector("[data-twitch-login-suggestions='true']");
            if (!(suggestions instanceof HTMLUListElement)) {
                return;
            }

            let requestId = 0;

            const clearSuggestions = () => {
                suggestions.innerHTML = "";
                suggestions.hidden = true;
            };

            const renderSuggestions = results => {
                suggestions.innerHTML = "";
                if (!Array.isArray(results) || results.length === 0) {
                    suggestions.hidden = true;
                    return;
                }

                for (const result of results) {
                    const item = document.createElement("li");
                    const button = document.createElement("button");
                    button.type = "button";
                    button.className = "autocomplete-option";
                    const primary = document.createElement("div");
                    primary.className = "autocomplete-primary";
                    const display = document.createElement("span");
                    display.textContent = result.displayName ?? "";
                    const login = document.createElement("span");
                    login.className = "mono";
                    login.textContent = result.login ?? "";
                    primary.append(display, login);
                    const secondary = document.createElement("div");
                    secondary.className = "autocomplete-secondary";
                    secondary.textContent = result.isLive ? "Live now" : "Offline";
                    button.append(primary, secondary);
                    button.addEventListener("click", () => {
                        input.value = result.login ?? "";
                        clearSuggestions();
                        input.focus();
                    });
                    item.appendChild(button);
                    suggestions.appendChild(item);
                }

                suggestions.hidden = false;
            };

            input.addEventListener("input", async () => {
                const query = input.value.trim();
                if (query.length < minLength) {
                    clearSuggestions();
                    return;
                }

                const currentRequestId = ++requestId;
                try {
                    const response = await fetch(`/api/twitch/channels/search?query=${encodeURIComponent(query)}`, {
                        cache: "no-store"
                    });

                    if (!response.ok) {
                        clearSuggestions();
                        return;
                    }

                    const results = await response.json();
                    if (currentRequestId !== requestId) {
                        return;
                    }

                    renderSuggestions(results);
                } catch {
                    clearSuggestions();
                }
            });

            input.addEventListener("blur", () => {
                window.setTimeout(clearSuggestions, 150);
            });
        });
    };

    const initCopyButtons = () => {
        document.querySelectorAll("[data-copy-button='true']").forEach(button => {
            if (!(button instanceof HTMLButtonElement)) {
                return;
            }

            const targetId = button.dataset.copyTarget;
            if (!targetId) {
                return;
            }

            button.addEventListener("click", async () => {
                const source = document.getElementById(targetId);
                if (!(source instanceof HTMLInputElement) && !(source instanceof HTMLTextAreaElement)) {
                    return;
                }

                try {
                    await navigator.clipboard.writeText(source.value);
                    const originalText = button.textContent;
                    button.textContent = "Copied";
                    window.setTimeout(() => {
                        button.textContent = originalText;
                    }, 1200);
                } catch {
                    source.focus();
                    source.select();
                }
            });
        });
    };

    const initPasswordToggles = () => {
        document.querySelectorAll("[data-password-toggle='true']").forEach(button => {
            if (!(button instanceof HTMLButtonElement)) {
                return;
            }

            const targetId = button.dataset.passwordTarget;
            if (!targetId) {
                return;
            }

            const target = document.getElementById(targetId);
            if (!(target instanceof HTMLInputElement)) {
                return;
            }

            const setLabel = () => {
                button.textContent = target.type === "password" ? "Show" : "Hide";
            };

            setLabel();
            button.addEventListener("click", () => {
                target.type = target.type === "password" ? "text" : "password";
                setLabel();
            });
        });
    };

    const initAutoSubmitOnChange = () => {
        document.querySelectorAll("[data-auto-submit='true']").forEach(element => {
            if (!(element instanceof HTMLSelectElement) && !(element instanceof HTMLInputElement)) {
                return;
            }

            element.addEventListener("change", () => {
                element.form?.requestSubmit();
            });
        });
    };

    const initAutoPruneToggles = () => {
        document.querySelectorAll("[data-auto-prune-toggle='true']").forEach(toggle => {
            if (!(toggle instanceof HTMLInputElement)) {
                return;
            }

            const targetId = toggle.dataset.autoPruneTarget;
            const target = targetId ? document.getElementById(targetId) : null;
            if (!(target instanceof HTMLInputElement)) {
                return;
            }

            const sync = () => {
                target.readOnly = !toggle.checked;
            };

            sync();
            toggle.addEventListener("change", sync);
        });
    };

    initPicker({
        namespace: "directory-picker",
        includeFiles: false
    });
    initPicker({
        namespace: "file-picker",
        includeFiles: true,
        searchPattern: "*.exe"
    });
    initTwitchLoginAutocomplete();
    initCopyButtons();
    initPasswordToggles();
    initAutoSubmitOnChange();
    initAutoPruneToggles();
    initRelativeTimes();
    initAutoRefresh();
})();
