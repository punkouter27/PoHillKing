// Unity's com.unity.dt.app-ui >= 2.1.12 preloads libAppUITextMateLib.a, whose Embind
// bindings were built against Emscripten 3.x and reference _emval_take_value / _emval_as,
// which Emscripten 4.x (Unity 6000.6) removed. None of the Po games invoke TextMate at
// runtime; these stubs satisfy wasm-ld alongside --bind in WebGLBuild.
#ifdef __EMSCRIPTEN__
#include <stdint.h>

uint32_t _emval_take_value(uint32_t type, uint32_t argv) {
    (void)type;
    (void)argv;
    return 0;
}

double _emval_as(uint32_t value, uint32_t returnType, uint32_t destructors) {
    (void)value;
    (void)returnType;
    (void)destructors;
    return 0.0;
}
#endif
