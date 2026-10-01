# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT
"""Patch SDL 3.4.2 so discrete devices can use up to 64 channels.

The shared converter cap is 8 on every platform. Opening the device also
rejects counts above 8 in CoreAudio, ALSA's named-map scan, and the Pulse
and PipeWire channel maps. WASAPI already takes the mix format's channel
count, so Windows only needs the converter and channel-map storage.
"""

from __future__ import annotations

from pathlib import Path


def _patch(path: Path, old: str, new: str, already: str) -> None:
    text = path.read_text(encoding="utf-8")
    if already in text:
        print(f"  already patched {path.name} ({already})")
        return
    if old not in text:
        raise SystemExit(f"SDL 64ch patch: pattern not found in {path}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")
    print(f"  patched {path}")


def _replace_if_present(path: Path, old: str, new: str, already: str) -> None:
    text = path.read_text(encoding="utf-8")
    if already in text or old not in text:
        return
    path.write_text(text.replace(old, new, 1), encoding="utf-8")
    print(f"  patched {path} ({already})")


def apply(src_root: Path) -> None:
    audio = src_root / "src" / "audio"
    _patch_converter(audio / "SDL_audiocvt.c")
    _patch_channel_map_storage(audio / "SDL_sysaudio.h")
    _patch_resampler(audio / "SDL_audioresample.c")
    _patch_coreaudio(audio / "coreaudio" / "SDL_coreaudio.m")
    _patch_alsa(audio / "alsa" / "SDL_alsa_audio.c")
    _patch_pulse(audio / "pulseaudio" / "SDL_pulseaudio.c")
    _patch_pipewire(audio / "pipewire" / "SDL_pipewire.c")


def _patch_converter(path: Path) -> None:
    old_count = """static bool SDL_IsSupportedChannelCount(const int channels)
{
    return ((channels >= 1) && (channels <= 8));
}"""
    new_count = """#ifndef SDL_MAX_CHANNELCOUNT
#define SDL_MAX_CHANNELCOUNT 64
#endif

static bool SDL_IsSupportedChannelCount(const int channels)
{
    return ((channels >= 1) && (channels <= SDL_MAX_CHANNELCOUNT));
}

// Copy overlapping channels and silence extras. Safe in-place (shrink forward, expand backward).
static void ConvertDiscreteChannelCount(float *dst, const float *src, int num_frames, int src_channels, int dst_channels)
{
    const int copy_ch = SDL_min(src_channels, dst_channels);
    if (src_channels >= dst_channels) {
        for (int i = 0; i < num_frames; i++) {
            for (int ch = 0; ch < copy_ch; ch++) {
                dst[ch] = src[ch];
            }
            src += src_channels;
            dst += dst_channels;
        }
    } else {
        src += (num_frames - 1) * src_channels;
        dst += (num_frames - 1) * dst_channels;
        for (int i = 0; i < num_frames; i++) {
            for (int ch = dst_channels - 1; ch >= copy_ch; ch--) {
                dst[ch] = 0.0f;
            }
            for (int ch = copy_ch - 1; ch >= 0; ch--) {
                dst[ch] = src[ch];
            }
            src -= src_channels;
            dst -= dst_channels;
        }
    }
}"""
    _patch(path, old_count, new_count, "SDL_MAX_CHANNELCOUNT")

    old_convert = """    if (channelconvert) {
        SDL_AudioChannelConverter channel_converter;
        SDL_AudioChannelConverter override = NULL;

        // SDL_IsSupportedChannelCount should have caught these asserts, or we added a new format and forgot to update the table.
        SDL_assert(src_channels <= SDL_arraysize(channel_converters));
        SDL_assert(dst_channels <= SDL_arraysize(channel_converters[0]));

        channel_converter = channel_converters[src_channels - 1][dst_channels - 1];
        SDL_assert(channel_converter != NULL);

        // swap in some SIMD versions for a few of these.
        if (channel_converter == SDL_ConvertStereoToMono) {
            #ifdef SDL_SSE3_INTRINSICS
            if (!override && SDL_HasSSE3()) { override = SDL_ConvertStereoToMono_SSE3; }
            #endif
        } else if (channel_converter == SDL_ConvertMonoToStereo) {
            #ifdef SDL_SSE_INTRINSICS
            if (!override && SDL_HasSSE()) { override = SDL_ConvertMonoToStereo_SSE; }
            #endif
        }

        if (override) {
            channel_converter = override;
        }

        void *buf = dstconvert ? scratch : dst;
        channel_converter((float *) buf, (const float *) src, num_frames);
        src = buf;
    }"""
    new_convert = """    if (channelconvert) {
        void *buf = dstconvert ? scratch : dst;
        if (src_channels > 8 || dst_channels > 8) {
            ConvertDiscreteChannelCount((float *)buf, (const float *)src, num_frames, src_channels, dst_channels);
        } else {
            SDL_AudioChannelConverter channel_converter;
            SDL_AudioChannelConverter override = NULL;

            SDL_assert(src_channels <= (int)SDL_arraysize(channel_converters));
            SDL_assert(dst_channels <= (int)SDL_arraysize(channel_converters[0]));

            channel_converter = channel_converters[src_channels - 1][dst_channels - 1];
            SDL_assert(channel_converter != NULL);

            if (channel_converter == SDL_ConvertStereoToMono) {
                #ifdef SDL_SSE3_INTRINSICS
                if (!override && SDL_HasSSE3()) { override = SDL_ConvertStereoToMono_SSE3; }
                #endif
            } else if (channel_converter == SDL_ConvertMonoToStereo) {
                #ifdef SDL_SSE_INTRINSICS
                if (!override && SDL_HasSSE()) { override = SDL_ConvertMonoToStereo_SSE; }
                #endif
            }

            if (override) {
                channel_converter = override;
            }

            channel_converter((float *) buf, (const float *) src, num_frames);
        }
        src = buf;
    }"""
    _patch(path, old_convert, new_convert, "src_channels > 8 || dst_channels > 8")

    _patch(
        path,
        "    void *channels_full[16];",
        "    void *channels_full[64]; // Cue2: planar pad up to SDL_MAX_CHANNELCOUNT",
        "channels_full[64]",
    )


def _patch_channel_map_storage(path: Path) -> None:
    old = "#define SDL_MAX_CHANNELMAP_CHANNELS 8  // !!! FIXME: if SDL ever supports more channels, clean this out and make those parts dynamic."
    new = "#define SDL_MAX_CHANNELMAP_CHANNELS 64  // Cue2: discrete devices. Per-stream storage, not a per-frame buffer."
    _patch(path, old, new, "SDL_MAX_CHANNELMAP_CHANNELS 64")


def _patch_resampler(path: Path) -> None:
    old = """    int i;
    Sint64 srcpos = *inout_resample_offset;
    ResampleFrameFunc resample_frame = ResampleFrame[chans - 1];

    SDL_assert(resample_rate > 0);"""
    new = """    int i;
    Sint64 srcpos = *inout_resample_offset;
    // Cue2: ResampleFrame[] covers 1-8. Slot 7 is always the generic SIMD/scalar kernel, which takes `chans`.
    ResampleFrameFunc resample_frame;
    if (chans >= 1 && chans <= (int)SDL_arraysize(ResampleFrame)) {
        resample_frame = ResampleFrame[chans - 1];
    } else {
        resample_frame = ResampleFrame[SDL_arraysize(ResampleFrame) - 1];
    }

    SDL_assert(resample_rate > 0);
    SDL_assert(chans >= 1);"""
    _patch(path, old, new, "ResampleFrame[] covers 1-8")


def _patch_coreaudio(path: Path) -> None:
    old = """    default:
        return SDL_SetError("Unsupported audio channels");
    }
    if (layout.mChannelLayoutTag != 0) {
        result = AudioQueueSetProperty(device->hidden->audioQueue, kAudioQueueProperty_ChannelLayout, &layout, sizeof(layout));
        CHECK_RESULT("AudioQueueSetProperty(kAudioQueueProperty_ChannelLayout)");
    }"""
    new = """    default:
        // Cue2: discrete in-order layout for interfaces above 7.1 (DVS, PlayAUDIO).
        if (device->spec.channels > 8 && device->spec.channels <= 64) {
            layout.mChannelLayoutTag = kAudioChannelLayoutTag_DiscreteInOrder | (UInt32)device->spec.channels;
            break;
        }
        return SDL_SetError("Unsupported audio channels");
    }
    if (layout.mChannelLayoutTag != 0) {
        result = AudioQueueSetProperty(device->hidden->audioQueue, kAudioQueueProperty_ChannelLayout, &layout, sizeof(layout));
        // Some queues take the ASBD channel count and reject a discrete layout tag.
        if (result != noErr && device->spec.channels > 8) {
            result = noErr;
        }
        CHECK_RESULT("AudioQueueSetProperty(kAudioQueueProperty_ChannelLayout)");
    }"""
    _patch(path, old, new, "kAudioChannelLayoutTag_DiscreteInOrder")


def _patch_alsa(path: Path) -> None:
    old_cap = """            if (target_chans_n > SDL_AUDIO_ALSA__CHMAP_CHANS_N_MAX) {
                return CHANS_N_NOT_CONFIGURED;
            }"""
    new_cap = """            // Cue2: named maps stop at 8. Discrete counts up to 64 are accepted below.
            if (target_chans_n > 64) {
                return CHANS_N_NOT_CONFIGURED;
            }"""
    _patch(path, old_cap, new_cap, "Discrete counts up to 64")

    old_cfg = """        // Here the alsa pcm is in SND_PCM_STATE_PREPARED state, let's figure out a good fit for
        // SDL channel map, it may request to change the target number of channels though.
        status = alsa_chmap_cfg(ctx);"""
    new_cfg = """        // Here the alsa pcm is in SND_PCM_STATE_PREPARED state, let's figure out a good fit for
        // SDL channel map, it may request to change the target number of channels though.
        // Cue2: the 8-slot map tables cannot describe discrete devices. Keep hardware order.
        if (ctx->chans_n > SDL_AUDIO_ALSA__CHMAP_CHANS_N_MAX) {
            return CHANS_N_CONFIGURED;
        }
        status = alsa_chmap_cfg(ctx);"""
    _patch(path, old_cfg, new_cfg, "8-slot map tables cannot describe")


def _patch_pulse(path: Path) -> None:
    old_sig = """static void PulseCreateChannelMap(pa_channel_map *pacmap, uint8_t channels)
{
    SDL_assert(channels <= PA_CHANNELS_MAX);

    pacmap->channels = channels;"""
    new_sig = """static bool PulseCreateChannelMap(pa_channel_map *pacmap, int channels)
{
    // Cue2: the Pulse map struct cannot exceed PA_CHANNELS_MAX (32 on typical builds).
    if (channels < 1 || channels > PA_CHANNELS_MAX) {
        return false;
    }

    pacmap->channels = channels;"""
    _patch(path, old_sig, new_sig, "the Pulse map struct cannot exceed")

    old_tail = """    case 8:
        COPY_CHANNEL_MAP(8);
        break;
    }

}

static bool PULSEAUDIO_OpenDevice(SDL_AudioDevice *device)"""
    new_tail = """    case 8:
        COPY_CHANNEL_MAP(8);
        break;
    default:
        // Cue2: discrete channels above 7.1, still within PA_CHANNELS_MAX.
        // Fill AUX positions here. Pulse is dlopened, so extra pa_* calls are not linked.
        for (int i = 0; i < channels; i++) {
            pacmap->map[i] = (pa_channel_position_t)(PA_CHANNEL_POSITION_AUX0 + i);
        }
        break;
    }
    return true;
}

static bool PULSEAUDIO_OpenDevice(SDL_AudioDevice *device)"""
    # A previous revision called pa_channel_map_init_extend, which is not in SDL's Pulse dlsym table.
    _replace_if_present(
        path,
        """        if (!pa_channel_map_init_extend(pacmap, (unsigned)channels, PA_CHANNEL_MAP_AUX)) {
            return false;
        }""",
        """        for (int i = 0; i < channels; i++) {
            pacmap->map[i] = (pa_channel_position_t)(PA_CHANNEL_POSITION_AUX0 + i);
        }""",
        "PA_CHANNEL_POSITION_AUX0",
    )
    _patch(path, old_tail, new_tail, "PA_CHANNEL_POSITION_AUX0")

    old_call = "    PulseCreateChannelMap(&pacmap, device->spec.channels);"
    new_call = """    if (!PulseCreateChannelMap(&pacmap, device->spec.channels)) {
        return SDL_SetError("pulseaudio: Unsupported channel count");
    }"""
    _patch(path, old_call, new_call, "pulseaudio: Unsupported channel count")


def _patch_pipewire(path: Path) -> None:
    old = """    case 8:
        COPY_CHANNEL_MAP(8);
        break;
    }

    // Pipewire natively supports all of SDL's sample formats"""
    new = """    case 8:
        COPY_CHANNEL_MAP(8);
        break;
    default:
        // Cue2: AUX positions for discrete devices, bounded by the spa position array.
        if (spec->channels > 8 && spec->channels <= (int)SDL_arraysize(info->position)) {
            for (int i = 0; i < spec->channels; i++) {
                info->position[i] = (enum spa_audio_channel)(SPA_AUDIO_CHANNEL_AUX0 + i);
            }
        }
        break;
    }

    // Pipewire natively supports all of SDL's sample formats"""
    _patch(path, old, new, "SPA_AUDIO_CHANNEL_AUX0")
