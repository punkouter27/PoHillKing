# MuJoCo 3.15.0 for WebGL

Static wasm libraries for the browser build (Builds/WebGL, the WebGL builder in Assets/Editor).
Unity links them into the player; org.mujoco's `MujocoLib` binds to `"__Internal"` on WebGL
instead of `"mujoco"`. The builder pins every `.a` here to WebGL only: Android's IL2CPP links
`.a` plugins too, and these are wasm.

Built 2026-10-27 with the Emscripten that ships inside Unity 6000.6.0f1 (4.0.20), with the
flags Unity compiles IL2CPP output with (wasm exceptions, SIMD, bulk memory; the link fails with any
other exception model), single-threaded (Unity WebGL has no pthreads):

    E="C:/Program Files/Unity/Hub/Editor/6000.6.0f1/Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten"
    export EM_CONFIG="$E/.emscripten"
    git clone --depth 1 --branch 3.15.0 https://github.com/google-deepmind/mujoco.git src
    # 3.5.0 only: delete the two " -pthread" in src/CMakeLists.txt lines 55-56 (hardcoded there)
    F="-mbulk-memory -mbulk-memory-opt -mnontrapping-fptoint -fwasm-exceptions -sSUPPORT_LONGJMP=wasm -msimd128 -sWASM_LEGACY_EXCEPTIONS=1"
    cmake -S src -B build -G Ninja -DCMAKE_TOOLCHAIN_FILE="$E/emscripten/cmake/Modules/Platform/Emscripten.cmake" \
      -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DMUJOCO_WASM_THREADS=OFF \
      -DMUJOCO_BUILD_TESTS_WASM=OFF -DMUJOCO_BUILD_STUDIO=OFF -DCMAKE_C_FLAGS="$F" -DCMAKE_CXX_FLAGS="$F"
    cmake --build build --target mujoco
    cp build/lib/*.a Assets/Plugins/WebGL/MuJoCo/

`libmujoco_render_stubs.a` holds empty bodies for the `mjr_*` / `mjui_*` functions: MuJoCo's
OpenGL renderer and UI are not built for Emscripten, but `MujocoLib` still binds them, and the
link fails on the missing symbols. Unity draws the scene itself and never calls them. They are
generated from this version's `mujoco.h` (each prototype, minus what libmujoco.a defines, with
a body returning a zeroed value) and compiled with the same `$F`.

A different Unity version means a different Emscripten: rebuild these with that version's.
