// WebSocketBridge.jslib - Browser-native WebSocket client bridge for Unity WebGPU

mergeInto(LibraryManager.library, {
    GS_WebSocketConnect: function(urlPtr, onFrameCallback, onStateCallback) {
        var url = UTF8ToString(urlPtr);
        if (Module._gsWebSocket) {
            try { Module._gsWebSocket.close(); } catch(e) {}
            Module._gsWebSocket = null;
        }

        try {
            var ws = new WebSocket(url);
            ws.binaryType = "arraybuffer";

            ws.onopen = function() {
                if (onStateCallback) {
                    {{{ makeDynCall('vi', 'onStateCallback') }}}(1); // 1 = Open
                }
            };

            ws.onclose = function() {
                if (onStateCallback) {
                    {{{ makeDynCall('vi', 'onStateCallback') }}}(2); // 2 = Closed
                }
                Module._gsWebSocket = null;
            };

            ws.onerror = function(err) {
                if (onStateCallback) {
                    {{{ makeDynCall('vi', 'onStateCallback') }}}(3); // 3 = Error
                }
            };

            ws.onmessage = function(event) {
                var data = new Uint8Array(event.data);
                var ptr = _malloc(data.length);
                HEAPU8.set(data, ptr);
                if (onFrameCallback) {
                    {{{ makeDynCall('vii', 'onFrameCallback') }}}(ptr, data.length);
                }
                _free(ptr);
            };

            Module._gsWebSocket = ws;
        } catch(e) {
            if (onStateCallback) {
                {{{ makeDynCall('vi', 'onStateCallback') }}}(3);
            }
        }
    },

    GS_WebSocketSend: function(dataPtr, length) {
        if (Module._gsWebSocket && Module._gsWebSocket.readyState === 1) {
            var data = HEAPU8.subarray(dataPtr, dataPtr + length);
            Module._gsWebSocket.send(data);
        }
    },

    GS_WebSocketClose: function() {
        if (Module._gsWebSocket) {
            try {
                Module._gsWebSocket.close();
            } catch(e) {}
            Module._gsWebSocket = null;
        }
    },

    GS_WebSocketGetState: function() {
        return Module._gsWebSocket ? Module._gsWebSocket.readyState : 3;
    }
});
