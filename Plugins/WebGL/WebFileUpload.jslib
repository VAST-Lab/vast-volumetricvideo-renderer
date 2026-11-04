// WebFileUpload.jslib - Browser file drag-and-drop & file picker for Unity WebGPU / itch.io

mergeInto(LibraryManager.library, {
    GS_InitFileDropTarget: function(gameObjectNamePtr, callbackMethodNamePtr) {
        var goName = UTF8ToString(gameObjectNamePtr);
        var cbName = UTF8ToString(callbackMethodNamePtr);
        var canvas = document.querySelector("#unity-canvas") || document.querySelector("canvas");
        if (!canvas) return;

        canvas.addEventListener("dragover", function(e) {
            e.preventDefault();
            e.stopPropagation();
            e.dataTransfer.dropEffect = "copy";
        }, false);

        canvas.addEventListener("drop", function(e) {
            e.preventDefault();
            e.stopPropagation();
            var files = e.dataTransfer.files;
            if (!files || files.length === 0) return;

            var file = files[0];
            var reader = new FileReader();
            reader.onload = function(evt) {
                var arrayBuffer = evt.target.result;
                var uint8Array = new Uint8Array(arrayBuffer);
                var ptr = _malloc(uint8Array.length);
                HEAPU8.set(uint8Array, ptr);

                // Store uploaded payload reference globally
                window._lastUploadedFileSize = uint8Array.length;
                window._lastUploadedFilePtr = ptr;
                window._lastUploadedFileName = file.name;

                // Send notification to Unity GameObject
                SendMessage(goName, cbName, file.name);
            };
            reader.readAsArrayBuffer(file);
        }, false);
    },

    GS_GetLastUploadedFilePtr: function() {
        return window._lastUploadedFilePtr || 0;
    },

    GS_GetLastUploadedFileSize: function() {
        return window._lastUploadedFileSize || 0;
    },

    GS_FreeUploadedFile: function() {
        if (window._lastUploadedFilePtr) {
            _free(window._lastUploadedFilePtr);
            window._lastUploadedFilePtr = 0;
            window._lastUploadedFileSize = 0;
        }
    }
});
