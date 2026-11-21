cmake_minimum_required(VERSION 3.20)

project(LineController LANGUAGES CXX)

enable_testing()

add_library(line_controller_core
    src/alarm_policy.cpp
    src/batch_counter.cpp
)

target_include_directories(line_controller_core PUBLIC include)
target_compile_features(line_controller_core PUBLIC cxx_std_20)

add_executable(line_controller_tests tests/controller_tests.cpp)
target_link_libraries(line_controller_tests PRIVATE line_controller_core)

add_test(NAME line_controller_tests COMMAND line_controller_tests)
