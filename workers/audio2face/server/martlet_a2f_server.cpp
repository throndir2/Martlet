// Martlet local Audio2Face service: NVIDIA's open-source Audio2Face-3D SDK (MIT) behind the NVIDIA ACE
// A2FControllerService.ProcessAudioStream gRPC contract, so the Martlet gateway's Audio2Face relay talks to it exactly
// like the Audio2Face-3D NIM. One regression model (claire, james or mark) runs on the GPU; requests are served one at
// a time. Only generated speech PCM arrives here, it stays in memory, and only blendshape weights go back.
//
// The SDK library is built with _GLIBCXX_USE_CXX11_ABI=0 and this program with the default ABI (gRPC), so no
// std::string ever crosses into the SDK: never call std::error_code::message() on an SDK error, only value().

#include "audio2face/audio2face.h"
#include "audio2x/cuda_utils.h"

#include "nvidia_ace.services.a2f_controller.v1.grpc.pb.h"

#include <cuda_runtime_api.h>
#include <grpcpp/grpcpp.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <csignal>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace {

using nvidia_ace::controller::v1::AnimationDataStream;
using nvidia_ace::controller::v1::AudioStream;
using nvidia_ace::services::a2f_controller::v1::A2FControllerService;
using Code = nvidia_ace::status::v1::Status;

constexpr std::uint32_t kMinRate = 8000;
constexpr std::uint32_t kMaxRate = 144000;
constexpr double kMaxSeconds = 120.0;
constexpr std::size_t kFramesPerMessage = 30;
constexpr double kPi = 3.14159265358979323846;

struct Destroyer {
  template <typename T> void operator()(T* item) const { if (item) item->Destroy(); }
};
template <typename T> using Owned = std::unique_ptr<T, Destroyer>;

void Log(const char* format, ...) __attribute__((format(printf, 1, 2)));
void Log(const char* format, ...) {
  va_list args;
  va_start(args, format);
  std::vfprintf(stderr, format, args);
  va_end(args);
  std::fputc('\n', stderr);
  std::fflush(stderr);
}

// Windowed-sinc (Blackman) resampler from the client's mono signed-16 PCM to the model's 16 kHz float input.
std::vector<float> ToModelRate(const std::vector<std::int16_t>& pcm, std::uint32_t rate, std::uint32_t target) {
  std::vector<float> input(pcm.size());
  for (std::size_t i = 0; i < pcm.size(); ++i) input[i] = static_cast<float>(pcm[i]) / 32768.0f;
  if (rate == target) return input;
  const double ratio = static_cast<double>(target) / rate;
  const double cutoff = std::min(1.0, ratio) * 0.95;
  const double half = std::ceil(12.0 / std::min(1.0, ratio));
  const auto outputSize = static_cast<std::size_t>(std::floor(input.size() * ratio));
  std::vector<float> output(outputSize);
  const auto last = static_cast<std::int64_t>(input.size()) - 1;
  for (std::size_t i = 0; i < outputSize; ++i) {
    const double position = i / ratio;
    const auto first = static_cast<std::int64_t>(std::ceil(position - half));
    const auto end = static_cast<std::int64_t>(std::floor(position + half));
    double sum = 0.0;
    for (auto k = std::max<std::int64_t>(first, 0); k <= std::min(end, last); ++k) {
      const double x = position - static_cast<double>(k);
      const double arg = kPi * cutoff * x;
      const double sinc = std::abs(arg) < 1e-9 ? 1.0 : std::sin(arg) / arg;
      const double window = 0.42 + 0.5 * std::cos(kPi * x / half) + 0.08 * std::cos(2.0 * kPi * x / half);
      sum += input[static_cast<std::size_t>(k)] * cutoff * sinc * window;
    }
    output[i] = static_cast<float>(sum);
  }
  return output;
}

struct Frame {
  std::int64_t timestamp;
  std::vector<float> weights;
};

class Engine {
 public:
  Engine(const char* modelPath, std::size_t fps, bool gpuSolver) {
    if (auto error = nva2x::SetCudaDeviceIfNeeded(0)) Fail("select CUDA device 0", error);
    nva2f::IRegressionModel::IGeometryModelInfo* geometryInfo = nullptr;
    nva2f::IRegressionModel::IBlendshapeSolveModelInfo* solveInfo = nullptr;
    bundle_.reset(nva2f::ReadRegressionBlendshapeSolveExecutorBundle(
        1, modelPath, Option, gpuSolver, fps, 1, &geometryInfo, &solveInfo));
    Owned<nva2f::IRegressionModel::IGeometryModelInfo> ownedGeometry(geometryInfo);
    Owned<nva2f::IRegressionModel::IBlendshapeSolveModelInfo> ownedSolve(solveInfo);
    if (!bundle_ || !solveInfo) {
      Log("Could not load the Audio2Face model %s (check the model files and the TensorRT engine).", modelPath);
      std::exit(2);
    }
    const auto parameters = solveInfo->GetExecutorCreationParameters(Option);
    // The SDK names ARKit poses in camelCase ("jawOpen"); the NIM protocol uses PascalCase ("JawOpen").
    for (const auto* part : {parameters.initializationSkinParams, parameters.initializationTongueParams}) {
      if (!part) continue;
      for (std::size_t i = 0; i < part->data.poseNamesSize; ++i) {
        std::string name(part->data.poseNames[i]);
        if (!name.empty()) name[0] = static_cast<char>(std::toupper(static_cast<unsigned char>(name[0])));
        names_.push_back(std::move(name));
      }
    }
    auto& executor = bundle_->GetExecutor();
    if (names_.empty() || names_.size() != executor.GetWeightCount()) {
      Log("The model has %zu pose names but %zu weights.", names_.size(), executor.GetWeightCount());
      std::exit(2);
    }
    rate_ = executor.GetSamplingRate();
    emotionSize_ = bundle_->GetEmotionAccumulator(0).GetEmotionSize();
    device_ = executor.GetResultType() == nva2f::IBlendshapeExecutor::ResultsType::DEVICE;
    if (device_) {
      if (auto error = executor.SetResultsCallback(&Engine::OnDevice, this)) Fail("set the results callback", error);
    } else if (auto error = executor.SetResultsCallback(&Engine::OnHost, this)) {
      Fail("set the results callback", error);
    }
    std::size_t numerator = 0, denominator = 1;
    executor.GetFrameRate(numerator, denominator);
    Log("Audio2Face model ready: %s, %zu blendshapes, %zu Hz input, %zu/%zu fps, %s blendshape solver.",
        modelPath, names_.size(), rate_, numerator, denominator, device_ ? "GPU" : "CPU");
    std::string list;
    for (const auto& name : names_) list += (list.empty() ? "" : " ") + name;
    Log("Blendshapes: %s", list.c_str());
    // One silent second up front so the first real sentence does not pay for CUDA and TensorRT warm-up.
    std::vector<Frame> warm;
    std::string reason;
    if (!Animate(std::vector<float>(rate_, 0.0f), warm, reason)) {
      Log("Warm-up failed: %s", reason.c_str());
      std::exit(2);
    }
  }

  const std::vector<std::string>& Names() const { return names_; }
  std::uint32_t Rate() const { return static_cast<std::uint32_t>(rate_); }

  // Animates one clip at the model's rate; returns false (with a reason) when the SDK fails.
  bool Animate(const std::vector<float>& audio, std::vector<Frame>& frames, std::string& reason) {
    std::lock_guard<std::mutex> lock(mutex_);
    frames_.clear();
    failed_ = 0;
    auto& executor = bundle_->GetExecutor();
    auto& audioIn = bundle_->GetAudioAccumulator(0);
    auto& emotionIn = bundle_->GetEmotionAccumulator(0);
    const auto stream = bundle_->GetCudaStream().Data();
    std::vector<float> neutral(emotionSize_, 0.0f);
    std::error_code error;
    const char* step = "";
    auto check = [&](const char* name, std::error_code code) {
      if (!code || error) return;
      error = code;
      step = name;
    };
    check("reset audio", audioIn.Reset());
    check("reset emotion", emotionIn.Reset());
    check("reset executor", executor.Reset(0));
    check("add emotion", emotionIn.Accumulate(0, nva2x::HostTensorFloatConstView{neutral.data(), neutral.size()}, stream));
    check("close emotion", emotionIn.Close());
    check("add audio", audioIn.Accumulate(nva2x::HostTensorFloatConstView{audio.data(), audio.size()}, stream));
    check("close audio", audioIn.Close());
    while (!error && nva2x::GetNbReadyTracks(executor) > 0) check("execute", executor.Execute(nullptr));
    if (!device_) check("wait", executor.Wait(0));
    if (error || failed_ != 0) {
      reason = std::string("Audio2Face SDK failed to ") + (error ? step : "solve blendshapes") +
          " (code " + std::to_string(error ? error.value() : failed_) + ")";
      return false;
    }
    std::sort(frames_.begin(), frames_.end(), [](const Frame& a, const Frame& b) { return a.timestamp < b.timestamp; });
    frames.swap(frames_);
    return true;
  }

 private:
  static constexpr auto Option = nva2f::IGeometryExecutor::ExecutionOption::SkinTongue;

  [[noreturn]] static void Fail(const char* step, std::error_code error) {
    Log("Audio2Face SDK could not %s (code %d).", step, error.value());
    std::exit(2);
  }

  static void OnHost(void* self, const nva2f::IBlendshapeExecutor::HostResults& results, std::error_code error) {
    auto& engine = *static_cast<Engine*>(self);
    std::lock_guard<std::mutex> lock(engine.framesMutex_);
    if (error) { engine.failed_ = error.value() == 0 ? -1 : error.value(); return; }
    engine.frames_.push_back({results.timeStampCurrentFrame,
        std::vector<float>(results.weights.Data(), results.weights.Data() + results.weights.Size())});
  }

  static bool OnDevice(void* self, const nva2f::IBlendshapeExecutor::DeviceResults& results) {
    auto& engine = *static_cast<Engine*>(self);
    std::vector<float> weights(results.weights.Size());
    if (cudaMemcpyAsync(weights.data(), results.weights.Data(), weights.size() * sizeof(float), cudaMemcpyDeviceToHost,
            results.cudaStream) != cudaSuccess ||
        cudaStreamSynchronize(results.cudaStream) != cudaSuccess) {
      std::lock_guard<std::mutex> lock(engine.framesMutex_);
      engine.failed_ = -2;
      return false;
    }
    std::lock_guard<std::mutex> lock(engine.framesMutex_);
    engine.frames_.push_back({results.timeStampCurrentFrame, std::move(weights)});
    return true;
  }

  Owned<nva2f::IBlendshapeExecutorBundle> bundle_;
  std::vector<std::string> names_;
  std::size_t rate_ = 16000;
  std::size_t emotionSize_ = 0;
  bool device_ = false;
  std::mutex mutex_;
  std::mutex framesMutex_;
  std::vector<Frame> frames_;
  int failed_ = 0;
};

class Service final : public A2FControllerService::Service {
 public:
  explicit Service(Engine& engine) : engine_(engine) {}

  grpc::Status ProcessAudioStream(grpc::ServerContext* context,
      grpc::ServerReaderWriter<AnimationDataStream, AudioStream>* stream) override {
    const auto started = std::chrono::steady_clock::now();
    const auto id = ++requests_;
    AudioStream message;
    if (!stream->Read(&message) || message.stream_part_case() != AudioStream::kAudioStreamHeader)
      return Reject(stream, "The first message must be the audio stream header.");
    const auto header = message.audio_stream_header();
    const auto& audio = header.audio_header();
    if (audio.audio_format() != nvidia_ace::audio::v1::AudioHeader::AUDIO_FORMAT_PCM || audio.channel_count() != 1 ||
        audio.bits_per_sample() != 16 || audio.samples_per_second() < kMinRate || audio.samples_per_second() > kMaxRate)
      return Reject(stream, "Only mono 16-bit PCM from 8 to 144 kHz is supported.");
    const auto rate = audio.samples_per_second();
    const auto maxSamples = static_cast<std::size_t>(kMaxSeconds * rate);
    std::vector<std::int16_t> pcm;
    while (stream->Read(&message)) {
      if (message.stream_part_case() == AudioStream::kEndOfAudio) break;
      if (message.stream_part_case() != AudioStream::kAudioWithEmotion)
        return Reject(stream, "Expected audio or the end of audio after the header.");
      const auto& bytes = message.audio_with_emotion().audio_buffer();
      if (bytes.size() % 2 != 0) return Reject(stream, "Audio buffers must hold whole 16-bit samples.");
      if (pcm.size() + bytes.size() / 2 > maxSamples) return Reject(stream, "The audio clip is longer than 120 seconds.");
      const auto offset = pcm.size();
      pcm.resize(offset + bytes.size() / 2);
      std::memcpy(pcm.data() + offset, bytes.data(), bytes.size());
    }
    if (context->IsCancelled()) return grpc::Status::CANCELLED;
    if (pcm.empty()) return Reject(stream, "No audio was sent.");

    const auto input = ToModelRate(pcm, rate, engine_.Rate());
    std::vector<Frame> frames;
    std::string reason;
    if (!engine_.Animate(input, frames, reason)) {
      Log("Request %llu failed: %s", static_cast<unsigned long long>(id), reason.c_str());
      return Reject(stream, reason);
    }
    if (context->IsCancelled()) return grpc::Status::CANCELLED;

    const auto& names = engine_.Names();
    std::vector<float> gain(names.size(), 1.0f), offset(names.size(), 0.0f);
    const auto& shapes = header.blendshape_params();
    for (std::size_t i = 0; i < names.size(); ++i) {
      if (auto found = shapes.bs_weight_multipliers().find(names[i]); found != shapes.bs_weight_multipliers().end())
        gain[i] = found->second;
      if (auto found = shapes.bs_weight_offsets().find(names[i]); found != shapes.bs_weight_offsets().end())
        offset[i] = found->second;
    }
    const bool clamp = shapes.has_enable_clamping_bs_weight() && shapes.enable_clamping_bs_weight();

    AnimationDataStream out;
    auto* outHeader = out.mutable_animation_data_stream_header();
    auto* outAudio = outHeader->mutable_audio_header();
    outAudio->set_audio_format(nvidia_ace::audio::v1::AudioHeader::AUDIO_FORMAT_PCM);
    outAudio->set_channel_count(1);
    outAudio->set_samples_per_second(rate);
    outAudio->set_bits_per_sample(16);
    for (const auto& name : names) outHeader->mutable_skel_animation_header()->add_blend_shapes(name);
    outHeader->set_start_time_code_since_epoch(
        std::chrono::duration<double>(std::chrono::system_clock::now().time_since_epoch()).count());
    if (!stream->Write(out)) return grpc::Status::CANCELLED;

    // Frames are timed on the model's 16 kHz clock; the client maps them back to its own sample clock and accepts
    // only times inside the clip it sent.
    const double duration = static_cast<double>(pcm.size()) / rate;
    double last = -1.0;
    std::size_t sent = 0;
    out.Clear();
    auto flush = [&]() {
      if (!out.has_animation_data()) return true;
      const bool ok = stream->Write(out);
      out.Clear();
      return ok;
    };
    for (const auto& frame : frames) {
      const double time = static_cast<double>(frame.timestamp) / engine_.Rate();
      if (time < 0.0 || time <= last || time > duration) continue;
      if (frame.weights.size() != names.size()) return Reject(stream, "The model returned a malformed frame.");
      auto* weights = out.mutable_animation_data()->mutable_skel_animation()->add_blend_shape_weights();
      weights->set_time_code(time);
      for (std::size_t i = 0; i < names.size(); ++i) {
        float value = frame.weights[i] * gain[i] + offset[i];
        if (!std::isfinite(value)) return Reject(stream, "The model returned a non-finite blendshape weight.");
        if (clamp) value = std::clamp(value, 0.0f, 1.0f);
        weights->add_values(value);
      }
      last = time;
      ++sent;
      if (out.animation_data().skel_animation().blend_shape_weights_size() >= static_cast<int>(kFramesPerMessage) &&
          !flush())
        return grpc::Status::CANCELLED;
    }
    if (!flush()) return grpc::Status::CANCELLED;
    if (sent == 0) return Reject(stream, "The clip was too short to animate.");

    out.mutable_event()->set_event_type(nvidia_ace::controller::v1::END_OF_A2F_AUDIO_PROCESSING);
    if (!stream->Write(out)) return grpc::Status::CANCELLED;
    out.Clear();
    out.mutable_status()->set_code(Code::SUCCESS);
    out.mutable_status()->set_message("sent all data");
    if (!stream->Write(out)) return grpc::Status::CANCELLED;
    const auto elapsed = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count();
    Log("Request %llu: %.2f s of %u Hz audio, %zu frames in %.0f ms.", static_cast<unsigned long long>(id), duration,
        rate, sent, elapsed);
    return grpc::Status::OK;
  }

 private:
  static grpc::Status Reject(grpc::ServerReaderWriter<AnimationDataStream, AudioStream>* stream, const std::string& why) {
    AnimationDataStream out;
    out.mutable_status()->set_code(Code::ERROR);
    out.mutable_status()->set_message(why);
    stream->Write(out);
    return grpc::Status(grpc::StatusCode::INVALID_ARGUMENT, why);
  }

  Engine& engine_;
  std::atomic<unsigned long long> requests_{0};
};

const char* Option(int argc, char** argv, const char* name, const char* fallback) {
  for (int i = 1; i + 1 < argc; ++i)
    if (std::strcmp(argv[i], name) == 0) return argv[i + 1];
  return fallback;
}

}  // namespace

int main(int argc, char** argv) {
  // PID 1 in its container: stop at once on docker stop instead of waiting to be killed.
  std::signal(SIGTERM, [](int) { std::_Exit(0); });
  std::signal(SIGINT, [](int) { std::_Exit(0); });
  const char* listen = Option(argc, argv, "--listen", "127.0.0.1:52000");
  const char* model = Option(argc, argv, "--model", nullptr);
  const long fps = std::strtol(Option(argc, argv, "--fps", "30"), nullptr, 10);
  const bool gpuSolver = std::strcmp(Option(argc, argv, "--solver", "gpu"), "cpu") != 0;
  if (!model || fps < 1 || fps > 120) {
    Log("Usage: martlet-a2f-server --model <model.json> [--listen 127.0.0.1:52000] [--fps 30] [--solver gpu|cpu]");
    return 64;
  }
  Engine engine(model, static_cast<std::size_t>(fps), gpuSolver);
  Service service(engine);
  grpc::ServerBuilder builder;
  builder.AddListeningPort(listen, grpc::InsecureServerCredentials());
  builder.RegisterService(&service);
  builder.SetMaxReceiveMessageSize(4 * 1024 * 1024);
  builder.SetMaxSendMessageSize(4 * 1024 * 1024);
  auto server = builder.BuildAndStart();
  if (!server) {
    Log("Could not listen on %s.", listen);
    return 3;
  }
  Log("Martlet Audio2Face service listening on %s.", listen);
  server->Wait();
  return 0;
}
