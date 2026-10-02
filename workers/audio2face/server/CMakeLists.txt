cmake_minimum_required(VERSION 3.24)
project(martlet-a2f-server LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# The open-source Audio2Face-3D SDK, built by the Dockerfile: AUDIO2X_SDK_DIR holds its include/ and lib/libaudio2x.so.
set(AUDIO2X_SDK_DIR "" CACHE PATH "Audio2Face-3D SDK build output (audio2x-sdk)")
find_package(CUDAToolkit 12.8 REQUIRED)
find_package(PkgConfig REQUIRED)
pkg_check_modules(GRPC REQUIRED IMPORTED_TARGET grpc++ protobuf)
find_program(PROTOC protoc REQUIRED)
find_program(GRPC_CPP_PLUGIN grpc_cpp_plugin REQUIRED)

set(PROTO_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../protos)
set(PROTOS
    nvidia_ace.a2f.v1 nvidia_ace.animation_data.v1 nvidia_ace.animation_id.v1 nvidia_ace.audio.v1
    nvidia_ace.controller.v1 nvidia_ace.emotion_with_timecode.v1 nvidia_ace.services.a2f_controller.v1
    nvidia_ace.status.v1)
set(GENERATED)
foreach(proto ${PROTOS})
  set(outputs ${CMAKE_CURRENT_BINARY_DIR}/${proto}.pb.cc ${CMAKE_CURRENT_BINARY_DIR}/${proto}.pb.h)
  set(arguments --cpp_out=${CMAKE_CURRENT_BINARY_DIR})
  if(proto STREQUAL "nvidia_ace.services.a2f_controller.v1")
    list(APPEND outputs ${CMAKE_CURRENT_BINARY_DIR}/${proto}.grpc.pb.cc ${CMAKE_CURRENT_BINARY_DIR}/${proto}.grpc.pb.h)
    list(APPEND arguments --grpc_out=${CMAKE_CURRENT_BINARY_DIR} --plugin=protoc-gen-grpc=${GRPC_CPP_PLUGIN})
  endif()
  add_custom_command(
      OUTPUT ${outputs}
      COMMAND ${PROTOC} -I ${PROTO_DIR} ${arguments} ${PROTO_DIR}/${proto}.proto
      DEPENDS ${PROTO_DIR}/${proto}.proto
      VERBATIM)
  list(APPEND GENERATED ${outputs})
endforeach()

add_executable(martlet-a2f-server martlet_a2f_server.cpp ${GENERATED})
target_include_directories(martlet-a2f-server PRIVATE ${CMAKE_CURRENT_BINARY_DIR} ${AUDIO2X_SDK_DIR}/include)
target_link_libraries(martlet-a2f-server PRIVATE
    ${AUDIO2X_SDK_DIR}/lib/libaudio2x.so CUDA::cudart PkgConfig::GRPC)
set_target_properties(martlet-a2f-server PROPERTIES BUILD_RPATH "\$ORIGIN" INSTALL_RPATH "\$ORIGIN")
