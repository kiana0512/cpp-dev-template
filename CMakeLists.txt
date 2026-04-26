cmake_minimum_required(VERSION 3.20)
project(aigc_game_cli VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)


set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

option(VH_BUILD_TESTS "Build protocol tests" ON)

# ---------------------------------------------------------------------------
# nlohmann/json
# ---------------------------------------------------------------------------
include(FetchContent)

find_package(nlohmann_json QUIET)
if (NOT nlohmann_json_FOUND)
    FetchContent_Declare(
            nlohmann_json
            GIT_REPOSITORY https://github.com/nlohmann/json.git
            GIT_TAG v3.11.3
    )
    FetchContent_MakeAvailable(nlohmann_json)
endif()


function(vh_set_compile_options target)
    if (MSVC)
        target_compile_options(${target} PRIVATE
                /W4
                /permissive-
                /utf-8
        )
    else()
        target_compile_options(${target} PRIVATE
                -Wall
                -Wextra
                -Wpedantic
                -finput-charset=UTF-8
                -fexec-charset=UTF-8
        )
    endif()
endfunction()


add_library(vh_protocol
        src/json_protocol.cpp
        src/packet_validator.cpp
        src/expression_mapper.cpp
        src/transport_client.cpp
        src/sender_service.cpp
)

target_include_directories(vh_protocol PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_link_libraries(vh_protocol PUBLIC
        nlohmann_json::nlohmann_json
)

target_compile_features(vh_protocol PUBLIC cxx_std_17)
vh_set_compile_options(vh_protocol)

if (WIN32)
    target_link_libraries(vh_protocol PUBLIC ws2_32)
endif()


add_executable(vh_demo_sender
        src/main.cpp
)

target_link_libraries(vh_demo_sender PRIVATE
        vh_protocol
)

vh_set_compile_options(vh_demo_sender)

if (WIN32)
    target_link_libraries(vh_demo_sender PRIVATE shell32)
endif()


if (VH_BUILD_TESTS)
    enable_testing()

    add_executable(vh_tests
            tests/test_packet_validator.cpp
    )

    target_link_libraries(vh_tests PRIVATE
            vh_protocol
    )

    vh_set_compile_options(vh_tests)

    add_test(NAME vh_tests COMMAND vh_tests)
endif()