cmake_minimum_required(VERSION 3.15)
project(PiuLinkSuiteResume LANGUAGES CXX)
add_library(PiuLinkSuiteResume SHARED SuiteResume.cpp)
target_compile_features(PiuLinkSuiteResume PRIVATE cxx_std_17)
target_compile_definitions(PiuLinkSuiteResume PRIVATE UNICODE _UNICODE NOMINMAX)
set_property(TARGET PiuLinkSuiteResume PROPERTY MSVC_RUNTIME_LIBRARY MultiThreaded)
target_include_directories(PiuLinkSuiteResume PRIVATE
    "${WIX_PACKAGES}/wixtoolset.bootstrapperapplicationapi/6.0.0/build/native/include"
    "${WIX_PACKAGES}/wixtoolset.wixstandardbootstrapperapplicationfunctionapi/6.0.0/lib/native/include")
target_link_libraries(PiuLinkSuiteResume PRIVATE shell32 ole32 oleaut32 uuid comsuppw)
