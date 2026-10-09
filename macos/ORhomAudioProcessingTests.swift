import Foundation

private enum AudioProcessingTestError: Error {
    case failed(String)
}

private func expectAudio(_ condition: @autoclosure () -> Bool, _ message: String) throws {
    if !condition() {
        throw AudioProcessingTestError.failed(message)
    }
}

private func sineAudio(amplitude: Float, count: Int = 16_000) -> [Float] {
    (0..<count).map { index in
        amplitude * Float(sin(2 * Double.pi * 200 * Double(index) / 16_000))
    }
}

func runORhomAudioProcessingTests() throws {
    let left = sineAudio(amplitude: 0.2, count: 320)
    let regularStereo = ORhomAudioProcessing.mixToMono(channels: [left, left])
    try expectAudio(regularStereo.channelFallbackIndex == nil, "In-phase stereo should use the ordinary mono mix.")
    try expectAudio(zip(regularStereo.samples, left).allSatisfy { abs($0 - $1) < 0.000001 }, "Ordinary stereo must preserve its signal.")

    let opposite = left.map { -$0 }
    let cancelledStereo = ORhomAudioProcessing.mixToMono(channels: [left, opposite])
    try expectAudio(cancelledStereo.channelFallbackIndex != nil, "Opposite channel phases must not cancel recorded speech.")
    try expectAudio(cancelledStereo.samples.contains { abs($0) > 0.19 }, "The fallback must retain an audible channel.")

    let partialCancellation = ORhomAudioProcessing.mixToMono(channels: [left, opposite.map { $0 * 0.9 }])
    try expectAudio(partialCancellation.channelFallbackIndex == 0, "Severe partial cancellation should use the stronger channel.")
    try expectAudio(partialCancellation.samples == left, "The recovered channel must remain intact.")

    let stableCancellation = ORhomAudioProcessing.mixToMono(
        channels: [left, opposite], preferredChannelIndex: 1
    )
    try expectAudio(stableCancellation.channelFallbackIndex == 1, "Equally good opposite channels must keep a stable selection across buffers.")
    try expectAudio(stableCancellation.samples == opposite, "A stable selected channel must preserve its polarity.")

    let balancedStereo = ORhomAudioProcessing.mixToMono(channels: [left, left.map { $0 * 0.5 }])
    try expectAudio(balancedStereo.channelFallbackIndex == nil, "Normal differences in channel volume must keep the average.")
    try expectAudio(zip(balancedStereo.samples, left).allSatisfy { abs($0 - $1 * 0.75) < 0.000001 }, "Regular channel averaging must stay unchanged.")

    let biasedStereo = ORhomAudioProcessing.mixToMono(channels: [left, opposite.map { $0 + 0.6 }])
    try expectAudio(biasedStereo.channelFallbackIndex != nil, "A channel DC bias must not hide phase cancellation.")

    let sparseChannels = ORhomAudioProcessing.mixToMono(channels: [left] + [[Float]](repeating: [Float](repeating: 0, count: 320), count: 3))
    try expectAudio(sparseChannels.samples == left, "Mostly empty multichannel input must not lose its one useful microphone channel.")
    let invalidMix = ORhomAudioProcessing.mixToMono(channels: [[.nan, .infinity], [0.1, -0.1]])
    try expectAudio(invalidMix.samples.allSatisfy(\.isFinite), "Invalid channel values must not poison the mono mix.")

    let silence = ORhomAudioProcessing.prepareForRecognition([Float](repeating: 0, count: 16_000))
    try expectAudio(silence.isNearSilence, "Digital silence must not be sent to Whisper.")
    try expectAudio(silence.samples.isEmpty, "Silence must not produce padded recognition input.")

    let dcOnly = ORhomAudioProcessing.prepareForRecognition([Float](repeating: 0.2, count: 16_000))
    try expectAudio(dcOnly.isNearSilence, "DC bias alone must not count as recorded speech.")

    let nearSilence = ORhomAudioProcessing.prepareForRecognition(sineAudio(amplitude: 0.00002))
    try expectAudio(nearSilence.isNearSilence, "Practically inaudible input must be rejected before decoding.")

    // A very quiet, brief utterance amid a long pause must survive the gate.
    let quietSpeech = sineAudio(amplitude: 0.0002, count: 3_200)
    let sparseInput = [Float](repeating: 0, count: 160_000) + quietSpeech
    let sparse = ORhomAudioProcessing.prepareForRecognition(sparseInput)
    try expectAudio(!sparse.isNearSilence, "A long pause must not average very quiet speech away.")
    try expectAudio(sparse.appliedGain > 1, "Quiet speech should receive a bounded volume lift.")
    try expectAudio(sparse.appliedGain <= 4, "Volume lift must stay within 12 dB.")

    // Quiet edge sounds remain in their original positions, including silence
    // between them; preprocessing must never act as a destructive noise gate.
    let edgeSpeech = sineAudio(amplitude: 0.0002, count: 320)
    let edgeInput = edgeSpeech + [Float](repeating: 0, count: 16_000) + edgeSpeech
    let edges = ORhomAudioProcessing.prepareForRecognition(edgeInput)
    let padding = ORhomAudioProcessing.edgePaddingFrames
    try expectAudio(edges.samples.count == edgeInput.count + 2 * padding, "Preparation must retain every input frame.")
    try expectAudio(abs(edges.samples[padding + 20]) > 0.0001, "The quiet first syllable must survive.")
    try expectAudio(abs(edges.samples[padding + edgeInput.count - 20]) > 0.0001, "The quiet final syllable must survive.")
    try expectAudio(edges.samples.prefix(padding).allSatisfy { $0 == 0 }, "The start margin must be silent.")
    try expectAudio(edges.samples.suffix(padding).allSatisfy { $0 == 0 }, "The end margin must be silent.")

    let biasedInput = sineAudio(amplitude: 0.01).map { $0 + 0.2 }
    let corrected = ORhomAudioProcessing.prepareForRecognition(biasedInput)
    let correctedSignal = corrected.samples[padding..<(padding + biasedInput.count)]
    let correctedMean = correctedSignal.reduce(0.0) { $0 + Double($1) } / Double(correctedSignal.count)
    try expectAudio(abs(correctedMean) < 0.000001, "DC removal must restore a centered signal.")

    let healthy = ORhomAudioProcessing.prepareForRecognition(sineAudio(amplitude: 0.4))
    try expectAudio(abs(healthy.appliedGain - 1) < 0.00001, "Healthy speech should preserve its existing volume.")

    var clickInput = sineAudio(amplitude: 0.01)
    clickInput[300] = 1
    let peakProtected = ORhomAudioProcessing.prepareForRecognition(clickInput)
    try expectAudio(peakProtected.samples.allSatisfy { $0.isFinite && abs($0) <= 0.95001 }, "A loud transient must never clip after normalization.")

    let invalid = ORhomAudioProcessing.prepareForRecognition([.nan, .infinity, -.infinity] + sineAudio(amplitude: 0.01))
    try expectAudio(invalid.samples.allSatisfy(\.isFinite), "Invalid hardware samples must never reach the recognizer.")

    try expectAudio(ORhomAudioProcessing.meterLevel(for: [0, 0, 0]) == 0, "A silent microphone must show a zero meter.")
    try expectAudio(ORhomAudioProcessing.meterLevel(for: [0.2, 0.2, 0.2]) == 0, "A DC offset must not create a false recording meter.")
    let quietMeter = ORhomAudioProcessing.meterLevel(for: sineAudio(amplitude: 0.005))
    let speechMeter = ORhomAudioProcessing.meterLevel(for: sineAudio(amplitude: 0.1))
    try expectAudio(quietMeter > 0.1 && quietMeter < speechMeter, "Quiet speech must be visible while louder speech shows a higher level.")
    try expectAudio(ORhomAudioProcessing.meterLevel(for: [.nan, .infinity]) == 0, "Malformed input must not break the meter.")
    print("PASS: Phase-safe mono mixing, audio preparation, quiet edge speech, silence protection and microphone meter")
}
