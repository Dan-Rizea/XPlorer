(function () {
    if (window._uiExpCleanup) window._uiExpCleanup();
    window._uiExpActive = true;
    window._uiExpCapture = null;
    window._uiExpCancelled = false;

    // Each frame owns its listeners and overlay. Frame events do not bubble to its parent.
    const overlay = document.createElement('div');
    overlay.id = '_ui_exp_overlay';
    overlay.style.cssText = 'all:initial;position:fixed;z-index:2147483647;pointer-events:none!important;border:2px solid #FF1744;background:rgba(255,23,68,.18);box-sizing:border-box;display:none;border-radius:3px;';
    const badge = document.createElement('div');
    badge.id = '_ui_exp_badge';
    badge.style.cssText = 'all:initial;position:absolute;left:0;background:#FF1744;color:white;font:11px Consolas,monospace;padding:3px 8px;white-space:pre;pointer-events:none!important;';
    overlay.appendChild(badge);
    (document.querySelector('dialog:modal') || document.body || document.documentElement).appendChild(overlay);
    // Native dialogs/popovers render above every z-index. Put the highlight in the
    // browser's top layer too so it remains visible over those controls.
    if (typeof overlay.showPopover === 'function') {
        overlay.popover = 'manual';
        overlay.showPopover();
    }

    let selected = null;
    let pointer = null;
    let candidates = null;
    const controls = 'input,textarea,select,button,[contenteditable]:not([contenteditable="false"])';
    const isInspector = node => node === overlay || overlay.contains(node);
    const shadowRootOf = node => {
        const root = node && node.getRootNode();
        return root && root.nodeType === 11 && root.host ? root : null;
    };
    const parentOf = node => node.parentNode && node.parentNode.host
        ? node.parentNode.host : node.parentNode;
    const labelOf = node => node.nodeType === 9 ? '#document'
        : node.tagName.toLowerCase() + (node.id ? '#' + node.id : '');

    function roots() {
        const result = [document];
        for (let i = 0; i < result.length; i++) {
            for (const el of result[i].querySelectorAll('*')) {
                if (!isInspector(el) && el.shadowRoot) result.push(el.shadowRoot);
            }
        }
        return result;
    }

    function hitStack(x, y, root = document, visited = new Set()) {
        const result = [];
        const stack = root.elementsFromPoint ? root.elementsFromPoint(x, y) : [];
        for (const el of stack) {
            if (visited.has(el) || isInspector(el)) continue;
            visited.add(el);
            if (el.shadowRoot) result.push(...hitStack(x, y, el.shadowRoot, visited));
            result.push(el);
        }
        return result;
    }

    function rectOf(node) {
        if (node.nodeType === 9) return { left: 0, top: 0, width: innerWidth, height: innerHeight };
        return node.getBoundingClientRect();
    }

    function containsPoint(rect, x, y) {
        return rect.width > 0 && rect.height > 0 &&
            x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom;
    }

    function collectCandidates() {
        if (!pointer) return [];
        const { x, y } = pointer;
        const stack = hitStack(x, y);
        const result = [];
        const add = node => {
            if (node && !isInspector(node) && !result.includes(node)) result.push(node);
        };
        // Keep the actual foreground element first, then offer the controls below it.
        add(stack[0]);
        const extras = [];
        for (const root of roots()) {
            for (const el of root.querySelectorAll('*')) {
                if (isInspector(el)) continue;
                if (containsPoint(rectOf(el), x, y)) extras.push(el);
            }
        }
        extras.filter(el => el.matches(controls)).forEach(add);
        stack.forEach(add);
        extras.forEach(add); // Also covers pointer-events:none and visibility:hidden elements.

        // Zero-sized/hidden controls can still be reached from their local wrapper.
        for (const el of [...result]) {
            if (el === document.body || el === document.documentElement) continue;
            const wrapper = el.matches(controls) ? el.parentElement : el;
            if (!wrapper || wrapper === document.body || wrapper === document.documentElement) continue;
            for (const child of (wrapper.shadowRoot || wrapper).querySelectorAll(controls)) {
                const rect = rectOf(child);
                if (!rect.width || !rect.height) add(child);
            }
        }
        add(document);
        return result;
    }

    function updateOverlay() {
        if (!selected || (selected.nodeType !== 9 && !selected.isConnected)) {
            overlay.style.display = 'none';
            return;
        }
        const modal = document.querySelector('dialog:modal');
        const container = modal || document.body || document.documentElement;
        if (!overlay.isConnected || (modal && !modal.contains(overlay))) {
            container.appendChild(overlay);
            if (typeof overlay.showPopover === 'function') overlay.showPopover();
        }
        let anchor = selected;
        let rect = rectOf(anchor);
        const hidden = !rect.width || !rect.height;
        while ((!rect.width || !rect.height) && parentOf(anchor)) {
            anchor = parentOf(anchor);
            rect = rectOf(anchor);
        }
        overlay.style.display = 'block';
        overlay.style.left = rect.left + 'px';
        overlay.style.top = rect.top + 'px';
        overlay.style.width = rect.width + 'px';
        overlay.style.height = rect.height + 'px';
        badge.style.top = rect.top < 48 ? '0px' : '-36px';
        const index = candidates ? candidates.indexOf(selected) : -1;
        badge.textContent = labelOf(selected) + (shadowRootOf(selected) ? ' [Shadow DOM]' : '') +
            (window !== window.top ? ' [Frame #document]' : '') +
            (hidden ? ' [Hidden; showing wrapper]' : '') +
            (index >= 0 ? ' [' + (index + 1) + '/' + candidates.length + ']' : '') +
            '\nAlt+Wheel / Tab: layers · ↑↓: parent/child · Click/Enter: capture · Esc: cancel';
    }

    function cycle(direction) {
        // Page scripts can replace controls or change their layout while indicating.
        // Rebuild on each deliberate cycle rather than retaining disconnected nodes.
        candidates = collectCandidates();
        if (!candidates.length) return;
        const index = candidates.indexOf(selected);
        selected = candidates[(index + direction + candidates.length) % candidates.length];
        updateOverlay();
    }

    function xpathLiteral(value) {
        if (!value.includes('"')) return '"' + value + '"';
        if (!value.includes("'")) return "'" + value + "'";
        return 'concat(' + value.split('"').map(part => '"' + part + '"').join(',\'"\',') + ')';
    }

    function generateXPath(el) {
        if (el.nodeType === 9) return '/';
        if (shadowRootOf(el)) return ''; // XPath cannot cross a shadow root; use the CSS chain.
        const tag = xpathStep(el);
        for (const attr of ['id', 'data-testid', 'data-test', 'data-qa', 'data-cy', 'name', 'placeholder', 'aria-label']) {
            const value = el.getAttribute(attr);
            if (!value) continue;
            const path = '//' + tag + '[@' + attr + '=' + xpathLiteral(value) + ']';
            try {
                if (document.evaluate(path, document, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null).snapshotLength === 1) return path;
            } catch (_) { }
        }
        if (['button', 'a', 'label', 'h1', 'h2', 'h3', 'span', 'p'].includes(tag)) {
            const text = (el.innerText || '').trim().replace(/\s+/g, ' ');
            if (text && text.length < 50) {
                const path = '//' + tag + '[normalize-space()=' + xpathLiteral(text) + ']';
                if (document.evaluate(path, document, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null).snapshotLength === 1) return path;
            }
        }
        const parts = [];
        for (let node = el; node && node.nodeType === 1; node = node.parentElement) {
            const siblings = node.parentNode ? Array.from(node.parentNode.children).filter(s => s.tagName === node.tagName) : [node];
            parts.unshift(xpathStep(node) + '[' + (siblings.indexOf(node) + 1) + ']');
        }
        return '/' + parts.join('/');
    }

    function xpathStep(node) {
        if (node.namespaceURI === 'http://www.w3.org/1999/xhtml') return node.localName;
        return '*[local-name()=' + xpathLiteral(node.localName) +
            ' and namespace-uri()=' + xpathLiteral(node.namespaceURI || '') + ']';
    }

    function generateCss(el) {
        if (el.nodeType === 9) return '';
        const root = el.getRootNode();
        const shadow = shadowRootOf(el);
        const prefix = shadow ? generateCss(shadow.host) + ' >>> ' : '';
        const tag = el.tagName.toLowerCase();
        const options = [];
        if (el.id) options.push('#' + CSS.escape(el.id));
        for (const attr of ['data-testid', 'data-test', 'name', 'placeholder', 'aria-label']) {
            const value = el.getAttribute(attr);
            if (value) options.push(tag + '[' + attr + '="' + CSS.escape(value) + '"]');
        }
        if (typeof el.className === 'string' && el.className.trim()) {
            options.push(tag + '.' + el.className.trim().split(/\s+/).map(c => CSS.escape(c)).join('.'));
        }
        for (const css of options) {
            try { if (root.querySelectorAll(css).length === 1) return prefix + css; } catch (_) { }
        }
        const parts = [];
        for (let node = el; node; node = node.parentElement) {
            const siblings = Array.from(node.parentNode.children).filter(s => s.tagName === node.tagName);
            parts.unshift(node.tagName.toLowerCase() + (siblings.length > 1 ? ':nth-of-type(' + (siblings.indexOf(node) + 1) + ')' : ''));
        }
        return prefix + parts.join(' > ');
    }

    function hierarchyOf(node) {
        const path = [];
        for (let curr = node; curr; curr = curr.parentNode || curr.host) {
            if (curr.nodeType === 9) path.unshift('#document');
            else if (curr.nodeType === 11 && curr.host) path.unshift('#shadow-root (' + curr.mode + ')');
            else if (curr.nodeType === 1) {
                let label = '<' + curr.tagName.toLowerCase();
                for (const attr of ['id', 'name', 'type', 'data-testid', 'class']) {
                    let value = curr.getAttribute(attr);
                    if (attr === 'class' && value) value = value.trim().split(/\s+/).slice(0, 2).join(' ');
                    if (value) label += ' ' + attr + '="' + value.replace(/"/g, '&quot;') + '"';
                }
                path.unshift(label + '>');
            }
        }
        return path.join(' ');
    }

    function capture() {
        if (!selected || (selected.nodeType !== 9 && !selected.isConnected)) return;
        const isDocument = selected.nodeType === 9;
        const attrs = { 'Tag Name': isDocument ? '#document' : selected.tagName.toLowerCase(), 'Document URL': document.URL };
        if (!isDocument) {
            for (const [attr, name] of Object.entries({ id: 'ID', name: 'Name', type: 'Type', class: 'Class', placeholder: 'Placeholder', 'aria-label': 'Aria Label', role: 'Role', 'data-testid': 'Data TestID', href: 'Href', src: 'Src', title: 'Title' })) {
                const value = selected.getAttribute(attr);
                if (value) attrs[name] = value;
            }
            if (selected.value !== undefined && selected.value !== '') attrs.Value = String(selected.value);
            const text = (selected.innerText || '').trim();
            if (text) attrs['Inner Text'] = text.slice(0, 150);
        }
        const rect = rectOf(selected);
        attrs['Bounding Box'] = Math.round(rect.left) + ', ' + Math.round(rect.top) + ' (' + Math.round(rect.width) + 'x' + Math.round(rect.height) + ')';
        const payload = {
            TagName: isDocument ? '#document' : selected.tagName,
            XPath: generateXPath(selected),
            Css: generateCss(selected),
            Hierarchy: hierarchyOf(selected),
            IsShadowDom: !!shadowRootOf(selected),
            FrameId: window._uiExpFrameId,
            Attributes: attrs
        };
        window._uiExpCleanup();
        // Read by the host in this frame's execution context. Page-level bindings in
        // older Puppeteer versions do not deliver callbacks from every isolated frame.
        window._uiExpCapture = JSON.stringify(payload);
    }

    function block(e) {
        e.preventDefault();
        e.stopPropagation();
        e.stopImmediatePropagation();
    }

    function onMouseMove(e) {
        if (!window._uiExpActive) return;
        // Tiny pointer jitter should not discard a layer chosen with the keyboard/wheel.
        if (pointer && Math.abs(pointer.x - e.clientX) < 3 && Math.abs(pointer.y - e.clientY) < 3) return;
        pointer = { x: e.clientX, y: e.clientY };
        // Keyboard events otherwise stay in the parent page when hovering a frame.
        window.focus();
        candidates = null;
        selected = hitStack(pointer.x, pointer.y)[0] || document;
        updateOverlay();
    }

    function onClick(e) {
        if (!window._uiExpActive) return;
        block(e);
        if (!pointer || Math.abs(pointer.x - e.clientX) >= 3 || Math.abs(pointer.y - e.clientY) >= 3) {
            pointer = { x: e.clientX, y: e.clientY };
            candidates = null;
            selected = hitStack(pointer.x, pointer.y)[0] || document;
        }
        capture();
    }

    function onWheel(e) {
        if (!window._uiExpActive || !e.altKey || !e.deltaY) return;
        block(e);
        if (!pointer || Math.abs(pointer.x - e.clientX) >= 3 || Math.abs(pointer.y - e.clientY) >= 3) {
            pointer = { x: e.clientX, y: e.clientY };
            candidates = null;
            selected = hitStack(pointer.x, pointer.y)[0] || document;
        }
        cycle(e.deltaY > 0 ? 1 : -1);
    }

    function onKeyDown(e) {
        if (!window._uiExpActive) return;
        if (!['Escape', 'Tab', 'ArrowUp', 'ArrowDown', 'Enter'].includes(e.key)) return;
        block(e);
        if (e.key === 'Escape') {
            window._uiExpCleanup();
            window._uiExpCancelled = true;
        } else if (e.key === 'Tab') cycle(e.shiftKey ? -1 : 1);
        else if (e.key === 'Enter') capture();
        else if (selected) {
            if (e.key === 'ArrowUp') selected = parentOf(selected) || selected;
            else {
                const root = selected.shadowRoot || selected;
                selected = selected.nodeType === 9 ? selected.documentElement
                    : root.querySelector(controls) || root.firstElementChild || selected;
            }
            updateOverlay();
        }
    }

    function onMouseOut(e) {
        if (!e.relatedTarget) overlay.style.display = 'none';
    }
    const blockedEvents = ['mousedown', 'mouseup', 'pointerdown', 'pointerup', 'dblclick', 'contextmenu'];
    window._uiExpCleanup = function () {
        window._uiExpActive = false;
        window.removeEventListener('mousemove', onMouseMove, true);
        window.removeEventListener('mouseout', onMouseOut, true);
        window.removeEventListener('click', onClick, true);
        window.removeEventListener('keydown', onKeyDown, true);
        window.removeEventListener('wheel', onWheel, true);
        window.removeEventListener('scroll', updateOverlay, true);
        blockedEvents.forEach(event => window.removeEventListener(event, block, true));
        overlay.remove();
    };
    window.addEventListener('mousemove', onMouseMove, true);
    window.addEventListener('mouseout', onMouseOut, true);
    window.addEventListener('click', onClick, true);
    window.addEventListener('keydown', onKeyDown, true);
    window.addEventListener('wheel', onWheel, { capture: true, passive: false });
    window.addEventListener('scroll', updateOverlay, true);
    blockedEvents.forEach(event => window.addEventListener(event, block, true));
})();
