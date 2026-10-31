# 编出验证台要用的几个真 VST3 包（正常 / 崩在 process / 卡在 process / 崩在加载 / 卡在加载）。
#
# 依赖 Steinberg 的 vst3sdk 检出（只需要 pluginterfaces / public.sdk / cmake 三个子模块）：
#   cmake -S tests/plugins/testvst3 -B build -DVST3_SDK=/path/to/vst3sdk
#   cmake --build build
#
# 产出的包在 build/bundles/ 下，build/bundles.txt 里逐行写着每个包的绝对路径。
#
# 【为什么不用 SDK 的 smtg_add_vst3plugin】它按 CMAKE_CONFIGURATION_TYPES 摆产物目录，
# 单配置生成器（Ninja/Makefiles，也就是 CI 上 Linux/macOS 用的那两个）上没有那个变量，
# 于是模块被丢在 build/VST3/ 下、而包目录是空的。我们只需要"编一个 MODULE 再按 VST3 的
# 目录约定摆好"，自己摆反而三平台一致、也没有多配置目录的歧义。
cmake_minimum_required(VERSION 3.20)

project(TuneLabTestVst3 VERSION 1.0.0 LANGUAGES C CXX)

# 只要 SDK 的接口与公共实现：不要 VSTGUI、不要 samples。
set(SMTG_ENABLE_VSTGUI_SUPPORT OFF CACHE BOOL "" FORCE)
set(SMTG_ADD_VSTGUI OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_PLUGIN_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_HOSTING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_CREATE_PLUGIN_LINK OFF CACHE BOOL "" FORCE)
set(SMTG_RUN_VST_VALIDATOR OFF CACHE BOOL "" FORCE)

if(NOT VST3_SDK OR NOT EXISTS "${VST3_SDK}/CMakeLists.txt")
  message(FATAL_ERROR "VST3_SDK must point at a vst3sdk checkout (with pluginterfaces/public.sdk submodules)")
endif()

add_subdirectory("${VST3_SDK}" "${CMAKE_BINARY_DIR}/vst3sdk")

# VST3 的包目录约定（三平台各一个）。
#
# 目录名必须与 SDK 的宿主加载器一致：Windows 是 Contents/x86_64-win（arm64-win），
# Linux 是 Contents/<uname -m>-linux，macOS 是 Contents/MacOS + Info.plist。
if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64|ARM64)$")
  set(TL_ARCH "aarch64")
else()
  set(TL_ARCH "x86_64")
endif()

if(SMTG_WIN)
  if(TL_ARCH STREQUAL "aarch64")
    set(TL_CONTENTS_SUBDIR "Contents/arm64-win")
  else()
    set(TL_CONTENTS_SUBDIR "Contents/x86_64-win")
  endif()
  set(TL_BINARY_SUFFIX ".vst3")
elseif(SMTG_MAC)
  set(TL_CONTENTS_SUBDIR "Contents/MacOS")
  set(TL_BINARY_SUFFIX "")
else()
  set(TL_CONTENTS_SUBDIR "Contents/${TL_ARCH}-linux")
  set(TL_BINARY_SUFFIX ".so")
endif()

set(TL_BUNDLE_ROOT "${CMAKE_BINARY_DIR}/bundles")
file(REMOVE "${CMAKE_BINARY_DIR}/bundles.txt")

# 目标名 | 变体号 | 插件显示名
set(variants
  "TestGain|0|TuneLab Test Gain"
  "TestCrashOnProcess|1|TuneLab Test Crash"
  "TestHangOnProcess|2|TuneLab Test Hang"
  "TestCrashOnLoad|3|TuneLab Test Load Crash"
  "TestHangOnLoad|4|TuneLab Test Load Hang")

foreach(entry IN LISTS variants)
  string(REPLACE "|" ";" parts "${entry}")
  list(GET parts 0 target)
  list(GET parts 1 variant)
  list(GET parts 2 name)

  set(bundle "${TL_BUNDLE_ROOT}/${target}.vst3")

  add_library(${target} MODULE testplugin.cpp)

  # 每个平台把包摆到同一个地方；同时把四个配置的目录都钉死 ——
  # 多配置生成器（VS / Xcode）否则会自己加一层 Release/、Debug/。
  set(_tl_out "${bundle}/${TL_CONTENTS_SUBDIR}")

  set_target_properties(${target} PROPERTIES
    PREFIX ""
    OUTPUT_NAME "${target}"
    SUFFIX "${TL_BINARY_SUFFIX}"
    LIBRARY_OUTPUT_DIRECTORY "${_tl_out}"
    RUNTIME_OUTPUT_DIRECTORY "${_tl_out}")

  foreach(_cfg DEBUG RELEASE RELWITHDEBINFO MINSIZEREL)
    set_target_properties(${target} PROPERTIES
      LIBRARY_OUTPUT_DIRECTORY_${_cfg} "${_tl_out}"
      RUNTIME_OUTPUT_DIRECTORY_${_cfg} "${_tl_out}")
  endforeach()

  target_compile_features(${target} PRIVATE cxx_std_17)

  # 源码里有中文注释（UTF-8，无 BOM）。MSVC 默认按系统代码页读，某些汉字的第二字节是
  # 0x5C（反斜杠），会把下一行接上来 —— 于是 #elif 就跑到了 #if 外面。必须显式 /utf-8。
  if(MSVC)
    target_compile_options(${target} PRIVATE /utf-8)
  endif()

  target_compile_definitions(${target} PRIVATE
    TL_TEST_VARIANT=${variant}
    TL_TEST_NAME="${name}")

  target_link_libraries(${target} PRIVATE sdk)

  if(SMTG_MAC)
    # macOS 的插件是一个 CFBundle：没有 Info.plist 就加载不了。
    file(WRITE "${bundle}/Contents/Info.plist"
"<?xml version=\"1.0\" encoding=\"UTF-8\"?>
<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">
<plist version=\"1.0\">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>English</string>
  <key>CFBundleExecutable</key><string>${target}</string>
  <key>CFBundleIdentifier</key><string>org.tunelab.debug.${target}</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>${target}</string>
  <key>CFBundlePackageType</key><string>BNDL</string>
  <key>CFBundleSignature</key><string>????</string>
  <key>CFBundleVersion</key><string>1.0.0</string>
  <key>CFBundleShortVersionString</key><string>1.0.0</string>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
")

    # arm64 上马马虎虎也要有签名：链接器会给 ad-hoc 签名，这里再显式盖一次，省得
    # 修改过二进制的构建机（如 strip 之后）加载不了。
    add_custom_command(TARGET ${target} POST_BUILD
      COMMAND codesign --force --sign - "${bundle}"
      COMMENT "[tl-test-vst3] codesign ${target}")
  endif()

  file(APPEND "${CMAKE_BINARY_DIR}/bundles.txt" "${bundle}\n")
endforeach()
