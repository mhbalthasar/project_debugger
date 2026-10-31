// 一个**最小但真实**的 VST3 效果器，专供验证台在 CI 里使用。
//
// 为什么自己编一个，而不用机器上现成的插件：
//   · GitHub 的 runner 上没有任何 VST3；
//   · 只有自己写的插件才能**故意崩溃 / 卡死**，而那正是隔离机制要扛住的两件事；
//   · 它的行为是确定的（增益），于是"音频真的穿过了桥"可以逐样本断言，
//     而不是"跑完没报错"。
//
// 行为：立体声进 / 立体声出，一个参数 Gain（0..1，默认 0.5），直接乘到样本上。
// state = 4 字节浮点 Gain。
//
// 变体（编译期定义 TL_TEST_VARIANT）：
//   0  正常
//   1  第一次 process 时 abort()          —— 真插件把桥弄崩
//   2  第一次 process 时永远睡下去         —— 真插件把桥卡死
//   3  initialize 时 abort()              —— 加载期崩溃
//   4  initialize 时永远睡下去            —— 加载期卡死
//
// 这个文件用 Steinberg 的 VST3 SDK 头文件（public.sdk / pluginterfaces）编，
// 因此它本身受 VST3 SDK 的双许可（GPLv3 或 Steinberg 专有）约束 —— 见同目录 README.md。
// 验证台的其余部分不链接 SDK。

#include "pluginterfaces/base/funknown.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/base/ustring.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "pluginterfaces/vst/ivstunits.h"
#include "public.sdk/source/main/pluginfactory.h"
#include "public.sdk/source/vst/vstaudioprocessoralgo.h"
#include "public.sdk/source/vst/vstaudioeffect.h"
#include "public.sdk/source/vst/vsteditcontroller.h"
#include "base/source/fstreamer.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <thread>

#ifndef TL_TEST_VARIANT
#define TL_TEST_VARIANT 0
#endif

#ifndef TL_TEST_NAME
#define TL_TEST_NAME "TuneLab Test Gain"
#endif

#define TL_STRING_(x) #x
#define TL_STRING(x) TL_STRING_(x)

namespace Steinberg {
namespace Vst {

// 每个变体一个独立的 class id：同一个宿主进程里先后装上两个变体时，
// 不会因为它们"看起来是同一个插件"而互相干扰。
#if TL_TEST_VARIANT == 1
static const FUID kProcessorUID (0x544c0001, 0x00010001, 0x80000001, 0x00000001);
static const FUID kControllerUID (0x544c0001, 0x00010001, 0x80000001, 0x00000002);
#elif TL_TEST_VARIANT == 2
static const FUID kProcessorUID (0x544c0002, 0x00010002, 0x80000002, 0x00000001);
static const FUID kControllerUID (0x544c0002, 0x00010002, 0x80000002, 0x00000002);
#elif TL_TEST_VARIANT == 3
static const FUID kProcessorUID (0x544c0003, 0x00010003, 0x80000003, 0x00000001);
static const FUID kControllerUID (0x544c0003, 0x00010003, 0x80000003, 0x00000002);
#elif TL_TEST_VARIANT == 4
static const FUID kProcessorUID (0x544c0004, 0x00010004, 0x80000004, 0x00000001);
static const FUID kControllerUID (0x544c0004, 0x00010004, 0x80000004, 0x00000002);
#else
static const FUID kProcessorUID (0x544c0000, 0x00010000, 0x80000000, 0x00000001);
static const FUID kControllerUID (0x544c0000, 0x00010000, 0x80000000, 0x00000002);
#endif

constexpr ParamID kGainId = 0;
constexpr double kGainDefault = 0.5;

void Log (const char* what)
{
	std::fprintf (stderr, "[tl-test-vst3] %s\n", what);
	std::fflush (stderr);
}

[[noreturn]] void HangForever ()
{
	Log ("hanging on purpose");
	for (;;)
		std::this_thread::sleep_for (std::chrono::seconds (1));
}

//------------------------------------------------------------------------
// 处理器
//------------------------------------------------------------------------
class TestProcessor : public AudioEffect
{
public:
	TestProcessor () { setControllerClass (kControllerUID); }

	static FUnknown* createInstance (void*) { return (IAudioProcessor*)new TestProcessor (); }

	tresult PLUGIN_API initialize (FUnknown* context) SMTG_OVERRIDE
	{
#if TL_TEST_VARIANT == 3
		Log ("crashing in initialize on purpose");
		std::abort ();
#endif
#if TL_TEST_VARIANT == 4
		HangForever ();
#endif

		tresult result = AudioEffect::initialize (context);
		if (result != kResultOk)
			return result;

		addAudioInput (STR16 ("Stereo In"), SpeakerArr::kStereo);
		addAudioOutput (STR16 ("Stereo Out"), SpeakerArr::kStereo);

		Log ("initialized");
		return kResultOk;
	}

	tresult PLUGIN_API setBusArrangements (SpeakerArrangement* inputs, int32 numIns,
	                                       SpeakerArrangement* outputs,
	                                       int32 numOuts) SMTG_OVERRIDE
	{
		// 只认立体声进 / 立体声出；其它组合明确拒绝（宿主会退回它自己的默认）。
		if (numIns == 1 && numOuts == 1 && SpeakerArr::getChannelCount (inputs[0]) == 2 &&
		    SpeakerArr::getChannelCount (outputs[0]) == 2)
		{
			getAudioInput (0)->setArrangement (inputs[0]);
			getAudioOutput (0)->setArrangement (outputs[0]);
			return kResultOk;
		}

		return kResultFalse;
	}

	tresult PLUGIN_API setupProcessing (ProcessSetup& setup) SMTG_OVERRIDE
	{
		return AudioEffect::setupProcessing (setup);
	}

	tresult PLUGIN_API canProcessSampleSize (int32 symbolicSampleSize) SMTG_OVERRIDE
	{
		return symbolicSampleSize == kSample32 ? kResultTrue : kResultFalse;
	}

	tresult PLUGIN_API process (ProcessData& data) SMTG_OVERRIDE
	{
#if TL_TEST_VARIANT == 1
		Log ("crashing in process on purpose");
		std::abort ();
#endif
#if TL_TEST_VARIANT == 2
		HangForever ();
#endif

		// 参数从宿主的队列里读（VST3 的标准路径）。
		if (IParameterChanges* changes = data.inputParameterChanges)
		{
			int32 count = changes->getParameterCount ();

			for (int32 i = 0; i < count; i++)
			{
				IParamValueQueue* queue = changes->getParameterData (i);
				if (queue == nullptr)
					continue;

				if (queue->getParameterId () != kGainId)
					continue;

				int32 points = queue->getPointCount ();
				ParamValue value = 0;
				int32 sampleOffset = 0;

				if (points > 0 && queue->getPoint (points - 1, sampleOffset, value) == kResultTrue)
					mGain = (float)value;
			}
		}

		if (data.numInputs == 0 || data.numOutputs == 0)
			return kResultOk;

		if (data.symbolicSampleSize != kSample32)
			return kResultOk;

		int32 channels = data.inputs[0].numChannels;
		if (data.outputs[0].numChannels < channels)
			channels = data.outputs[0].numChannels;

		uint32 bytes = getSampleFramesSizeInBytes (processSetup, data.numSamples);
		Sample32** in = (Sample32**)getChannelBuffersPointer (processSetup, data.inputs[0]);
		Sample32** out = (Sample32**)getChannelBuffersPointer (processSetup, data.outputs[0]);

		if (in[0] == out[0] && channels == data.inputs[0].numChannels)
		{
			// 原地：直接乘。
			for (int32 c = 0; c < channels; c++)
				for (int32 i = 0; i < data.numSamples; i++)
					out[c][i] = in[c][i] * mGain;
		}
		else
		{
			for (int32 c = 0; c < channels; c++)
				for (int32 i = 0; i < data.numSamples; i++)
					out[c][i] = in[c][i] * mGain;

			for (int32 c = channels; c < data.outputs[0].numChannels; c++)
				std::memset (out[c], 0, bytes);
		}

		data.outputs[0].silenceFlags = 0;

		mProcessed++;
		if (mProcessed == 1)
			Log ("processed the first block");

		return kResultOk;
	}

	tresult PLUGIN_API getState (IBStream* state) SMTG_OVERRIDE
	{
		IBStreamer streamer (state, kLittleEndian);
		streamer.writeFloat (mGain);
		return kResultOk;
	}

	tresult PLUGIN_API setState (IBStream* state) SMTG_OVERRIDE
	{
		IBStreamer streamer (state, kLittleEndian);
		float gain = 0;

		if (!streamer.readFloat (gain))
			return kResultFalse;

		mGain = gain;
		return kResultOk;
	}

private:
	float mGain = (float)kGainDefault;
	int32 mProcessed = 0;
};

//------------------------------------------------------------------------
// 编辑控制器
//------------------------------------------------------------------------
class TestController : public EditController
{
public:
	static FUnknown* createInstance (void*) { return (IEditController*)new TestController (); }

	tresult PLUGIN_API initialize (FUnknown* context) SMTG_OVERRIDE
	{
		tresult result = EditController::initialize (context);
		if (result != kResultOk)
			return result;

		// 一个参数：Gain，0..1，默认 0.5。VST3 的参数值本来就是归一化的，
		// 于是"归一化值"和"线性增益"在这里是同一个数 —— 断言可以逐样本对。
		parameters.addParameter (STR16 ("Gain"), nullptr, 0, kGainDefault,
		                         ParameterInfo::kCanAutomate, kGainId);

		Log ("controller initialized");
		return kResultOk;
	}

	IPlugView* PLUGIN_API createView (FIDString /*name*/) SMTG_OVERRIDE
	{
		// 没有界面：HasEditor 报 false，宿主退回参数面板（验证台没有 GUI）。
		return nullptr;
	}

	tresult PLUGIN_API setComponentState (IBStream* state) SMTG_OVERRIDE
	{
		IBStreamer streamer (state, kLittleEndian);
		float gain = 0;

		if (!streamer.readFloat (gain))
			return kResultFalse;

		setParamNormalized (kGainId, gain);
		return kResultOk;
	}
};

} // namespace Vst
} // namespace Steinberg

//------------------------------------------------------------------------
// 工厂
//------------------------------------------------------------------------
BEGIN_FACTORY_DEF ("TuneLab", "https://github.com/mhbalthasar/project_debugger", "")

DEF_CLASS2 (INLINE_UID_FROM_FUID (Steinberg::Vst::kProcessorUID), PClassInfo::kManyInstances,
            kVstAudioEffectClass, TL_TEST_NAME, Vst::kDistributable, "Fx",
            "1.0.0", kVstVersionString, Steinberg::Vst::TestProcessor::createInstance)

DEF_CLASS2 (INLINE_UID_FROM_FUID (Steinberg::Vst::kControllerUID), PClassInfo::kManyInstances,
            kVstComponentControllerClass, TL_TEST_NAME " Controller", 0, "", "1.0.0",
            kVstVersionString, Steinberg::Vst::TestController::createInstance)

END_FACTORY
