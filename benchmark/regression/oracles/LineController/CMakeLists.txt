cmake_minimum_required(VERSION 3.20)

project(LineControllerOracle LANGUAGES CXX)

if(NOT DEFINED WORKSPACE_ROOT)
    message(FATAL_ERROR "WORKSPACE_ROOT is required")
endif()
if(NOT DEFINED ORACLE_CASE_ID)
    message(FATAL_ERROR "ORACLE_CASE_ID is required")
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
string(TOUPPER "${ORACLE_CASE_ID}" ORACLE_CASE_DEFINE)
string(REPLACE "-" "_" ORACLE_CASE_DEFINE "${ORACLE_CASE_DEFINE}")
target_compile_definitions(line_controller_oracle_tests PRIVATE
    "ORACLE_CASE_${ORACLE_CASE_DEFINE}"
)
