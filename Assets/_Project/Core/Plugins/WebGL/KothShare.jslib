mergeInto(LibraryManager.library, {
    KothShareImage: function (namePtr, b64Ptr) {
        var name = UTF8ToString(namePtr) || 'koth-photo.png';
        var b64 = UTF8ToString(b64Ptr);
        try {
            var bin = atob(b64);
            var arr = new Uint8Array(bin.length);
            for (var i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
            var blob = new Blob([arr], { type: 'image/png' });
            var file = (typeof File === 'function') ? new File([blob], name, { type: 'image/png' }) : null;
            if (file && navigator.canShare && navigator.canShare({ files: [file] })) {
                navigator.share({ files: [file], title: 'PoKingHill Highlight' }).catch(function () {});
                return;
            }
            var a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = name;
            document.body.appendChild(a);
            a.click();
            setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1500);
        } catch (e) {}
    },

    KothRecordClip: function (durationMs, titlePtr) {
        var title = UTF8ToString(titlePtr) || 'PoKingHill KO';
        try {
            var canvas = document.querySelector('canvas');
            if (!canvas || !canvas.captureStream || typeof MediaRecorder === 'undefined') return 0;
            var chunks = [];
            var stream = canvas.captureStream(30);
            var rec = new MediaRecorder(stream, { mimeType: MediaRecorder.isTypeSupported('video/webm') ? 'video/webm' : '' });
            rec.ondataavailable = function (ev) { if (ev.data && ev.data.size > 0) chunks.push(ev.data); };
            rec.onstop = function () {
                if (!chunks.length) return;
                var blob = new Blob(chunks, { type: 'video/webm' });
                var file = (typeof File === 'function') ? new File([blob], 'koth-ko.webm', { type: 'video/webm' }) : null;
                if (file && navigator.canShare && navigator.canShare({ files: [file] })) {
                    navigator.share({ files: [file], title: title }).catch(function () {});
                    return;
                }
                var a = document.createElement('a');
                a.href = URL.createObjectURL(blob);
                a.download = 'koth-ko.webm';
                document.body.appendChild(a);
                a.click();
                setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 2000);
            };
            rec.start();
            setTimeout(function () { if (rec.state !== 'inactive') rec.stop(); }, Math.max(800, Math.min(8000, durationMs)));
            return 1;
        } catch (e) { return 0; }
    }
});
