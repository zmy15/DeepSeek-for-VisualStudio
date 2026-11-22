cmake_minimum_required(VERSION 3.20)

project(LineControllerOracle LANGUAGES CXX)

if(NOT DEFINED WORKSPACE_ROOT)
    message(FATAL_ERROR "WORKSPACE_ROOT is required")
endif()

add_executable(line_controller_oracle_tests oracle_tests.cpp)
target_sources(line_controller_oracle_tests PRIVATE
    "${WORKSPACE_ROOT}/src/alarm_policy.cpp"
    "${WORKSPACE_ROOT}/src/batch_counter.cpp"
)
target_include_directories(line_controller_oracle_tests PRIVATE
    "${WORKSPACE_ROOT}/include"
)
target_compile_features(line_controller_oracle_tests PRIVATE cxx_std_20)
