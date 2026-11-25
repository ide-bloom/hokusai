cmake_minimum_required(VERSION 3.25)
project(hokusai_agent VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the agent loads into arbitrary legacy processes that may not have a matching VC runtime.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

if(CMAKE_SIZEOF_VOID_P EQUAL 8)
  set(HK_BITS 64)
else()
  set(HK_BITS 32)
endif()

find_path(DETOURS_INCLUDE_DIRS "detours/detours.h" REQUIRED)
# vcpkg installs release and debug builds side by side; pick the one matching each configuration.
find_library(DETOURS_LIBRARY_RELEASE NAMES detours PATHS "${VCPKG_INSTALLED_DIR}/${VCPKG_TARGET_TRIPLET}/lib" NO_DEFAULT_PATH REQUIRED)
find_library(DETOURS_LIBRARY_DEBUG NAMES detours PATHS "${VCPKG_INSTALLED_DIR}/${VCPKG_TARGET_TRIPLET}/debug/lib" NO_DEFAULT_PATH)
include(SelectLibraryConfigurations)
select_library_configurations(DETOURS)

add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
add_compile_options(/W4 /WX /permissive- /utf-8)

add_library(hokusai_agent SHARED src/agent.cpp src/hooks.cpp src/json.cpp src/ring.cpp src/agent.def)
set_target_properties(hokusai_agent PROPERTIES OUTPUT_NAME "hokusai_agent${HK_BITS}")
target_include_directories(hokusai_agent PRIVATE ${DETOURS_INCLUDE_DIRS} src)
target_link_libraries(hokusai_agent PRIVATE ${DETOURS_LIBRARY} odbc32)

add_executable(hokusai_launcher src/launcher.cpp)
set_target_properties(hokusai_launcher PROPERTIES OUTPUT_NAME "hokusai_launcher${HK_BITS}")
target_include_directories(hokusai_launcher PRIVATE ${DETOURS_INCLUDE_DIRS} src)
target_link_libraries(hokusai_launcher PRIVATE ${DETOURS_LIBRARY} shell32)

install(TARGETS hokusai_agent hokusai_launcher RUNTIME DESTINATION .)
