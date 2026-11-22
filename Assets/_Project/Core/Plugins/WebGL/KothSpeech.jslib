// Commentator voice on the web (VoiceCaster): the browser's own speechSynthesis. Only voices that run on the device
// (localService) are used, so no caption text leaves the browser; with none installed the game stays silent.
mergeInto(LibraryManager.library, {
  KothSpeechAvailable: function () {
    return (typeof window !== 'undefined' && 'speechSynthesis' in window && typeof SpeechSynthesisUtterance !== 'undefined') ? 1 : 0;
  },

  KothSpeechBusy: function () {
    return (typeof window !== 'undefined' && window.speechSynthesis && (window.speechSynthesis.speaking || window.speechSynthesis.pending)) ? 1 : 0;
  },

  KothSpeechStop: function () {
    if (typeof window !== 'undefined' && window.speechSynthesis) window.speechSynthesis.cancel();
  },

  // text, rate (0.5..2), pitch (0..2), volume (0..1), flush (1 = drop what is being said)
  KothSpeechSay: function (textPtr, rate, pitch, volume, flush) {
    if (typeof window === 'undefined' || !window.speechSynthesis) return 0;
    var synth = window.speechSynthesis;
    var voices = synth.getVoices() || [];
    var lang = (navigator.language || 'en').toLowerCase();
    var pick = null, score = -1;
    for (var i = 0; i < voices.length; i++) {
      var v = voices[i];
      if (!v.localService) continue;                       // a remote voice would send the text to its server
      var l = (v.lang || '').toLowerCase();
      var s = l.indexOf('en') === 0 ? 2 : 0;               // the lines are English
      if (l === lang) s += 1;
      if (v.default) s += 1;
      if (s > score) { score = s; pick = v; }
    }
    if (!pick || score < 2) return 0;
    if (flush) synth.cancel();
    var u = new SpeechSynthesisUtterance(UTF8ToString(textPtr));
    u.voice = pick; u.lang = pick.lang; u.rate = rate; u.pitch = pitch; u.volume = volume;
    synth.speak(u);
    return 1;
  }
});
