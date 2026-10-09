import Foundation

/// Conservative preparation for Whisper's 16 kHz mono input. It deliberately
/// retains every input frame so that pauses and quiet first/last syllables survive.
enum ORhomAudioProcessing {
    static let sampleRate = 16_000
    static let edgePaddingFrames = 4_800
    static let maximumGain: Float = 4
    static let nearSilenceRMS: Float = 0.00005

    struct PreparedAudio {
        let samples: [Float]
        let appliedGain: Float
        let peakFrameRMS: Float
        let isNearSilence: Bool
    }

    struct MonoMix {
        let samples: [Float]
        let channelFallbackIndex: Int?
    }

    /// Average ordinary stereo/multichannel input, but rescue speech when
    /// opposing channel phases would cancel most of its energy in the average.
    static func mixToMono(
        channels: [[Float]],
        preferredChannelIndex: Int? = nil
    ) -> MonoMix {
        guard let frameCount = channels.map(\.count).min(), frameCount > 0 else {
            return MonoMix(samples: [], channelFallbackIndex: nil)
        }
        let cleanChannels = channels.map { channel in
            channel.prefix(frameCount).map { sample in
                sample.isFinite ? min(1, max(-1, sample)) : 0
            }
        }
        guard cleanChannels.count > 1 else {
            return MonoMix(samples: cleanChannels[0], channelFallbackIndex: nil)
        }
        var mixed = [Float](repeating: 0, count: frameCount)
        for channel in cleanChannels {
            for frame in mixed.indices {
                mixed[frame] += channel[frame] / Float(cleanChannels.count)
            }
        }

        // DC bias is not speech energy and must not influence channel choice.
        func signalEnergy(_ samples: [Float]) -> Double {
            let mean = samples.reduce(0.0) { $0 + Double($1) } / Double(samples.count)
            return samples.reduce(0.0) { total, sample in
                let centered = Double(sample) - mean
                return total + centered * centered
            }
        }
        let channelEnergy = cleanChannels.map(signalEnergy)
        var strongestIndex = 0
        for index in channelEnergy.indices where channelEnergy[index] > channelEnergy[strongestIndex] {
            strongestIndex = index
        }
        let strongestEnergy = channelEnergy[strongestIndex]
        guard strongestEnergy > 0, signalEnergy(mixed) < strongestEnergy * 0.1 else {
            return MonoMix(samples: mixed, channelFallbackIndex: nil)
        }

        // Keep a recently chosen channel if its level is almost as good; this
        // avoids polarity changes between buffers with equal opposite signals.
        let selectedIndex: Int
        if let preferredChannelIndex,
           channelEnergy.indices.contains(preferredChannelIndex),
           channelEnergy[preferredChannelIndex] >= strongestEnergy * 0.8 {
            selectedIndex = preferredChannelIndex
        } else {
            selectedIndex = strongestIndex
        }
        return MonoMix(samples: cleanChannels[selectedIndex], channelFallbackIndex: selectedIndex)
    }

    static func prepareForRecognition(_ input: [Float]) -> PreparedAudio {
        guard !input.isEmpty else {
            return PreparedAudio(
                samples: [], appliedGain: 1, peakFrameRMS: 0, isNearSilence: true
            )
        }

        // Malformed hardware/file values must never reach Whisper or the meter.
        var samples = input.map { sample in
            sample.isFinite ? min(1, max(-1, sample)) : 0
        }
        let dcOffset = Float(samples.reduce(0.0) { $0 + Double($1) } / Double(samples.count))
        var peak: Float = 0
        for index in samples.indices {
            samples[index] -= dcOffset
            peak = max(peak, abs(samples[index]))
        }

        // Look at the loudest 20 ms rather than averaging speech away during a
        // long pause. This only rejects near-digital silence; it is not a VAD.
        let frameLength = sampleRate / 50
        var peakFrameRMS: Float = 0
        for start in stride(from: 0, to: samples.count, by: frameLength) {
            let end = min(samples.count, start + frameLength)
            let energy = samples[start..<end].reduce(0.0) { $0 + Double($1) * Double($1) }
            peakFrameRMS = max(peakFrameRMS, Float(sqrt(energy / Double(end - start))))
        }
        guard peakFrameRMS >= nearSilenceRMS, peak > 0 else {
            return PreparedAudio(
                samples: [], appliedGain: 1, peakFrameRMS: peakFrameRMS, isNearSilence: true
            )
        }

        // Keep already healthy speech at its original volume. Quiet recordings
        // gain at most 12 dB, constrained by the actual peak to avoid clipping.
        let desiredGain = min(maximumGain, max(1, 0.08 / peakFrameRMS))
        let gain = min(desiredGain, 0.95 / peak)
        for index in samples.indices {
            samples[index] *= gain
        }

        // Whisper gets a little context at both edges even when the hotkey is
        // released immediately after the final word. No recorded audio is cut.
        let padding = [Float](repeating: 0, count: edgePaddingFrames)
        return PreparedAudio(
            samples: padding + samples + padding,
            appliedGain: gain,
            peakFrameRMS: peakFrameRMS,
            isNearSilence: false
        )
    }

    /// A real signal meter: silence is zero, and quiet speech is visible on a
    /// logarithmic scale from -60 dBFS to 0 dBFS.
    static func meterLevel(for input: [Float]) -> Float {
        guard !input.isEmpty else { return 0 }
        let samples = input.map { sample in
            sample.isFinite ? min(1, max(-1, sample)) : 0
        }
        let mean = samples.reduce(0.0) { $0 + Double($1) } / Double(samples.count)
        let energy = samples.reduce(0.0) { sum, sample in
            let centered = Double(sample) - mean
            return sum + centered * centered
        }
        let rms = sqrt(energy / Double(samples.count))
        guard rms > 0 else { return 0 }
        return Float(min(1, max(0, (20 * log10(rms) + 60) / 60)))
    }
}
