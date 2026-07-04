using System;
using System.Text;

namespace HapticAI.Synth
{
    /// <summary>
    /// Tiny RIFF/WAVE reader+writer for the one format that matters here: uncompressed PCM,
    /// 16-bit or 32-bit float, mono or multi-channel (channels are averaged to mono). The
    /// reference HaptiGen server returns audio/wav; the mock synth and tests round-trip through
    /// <see cref="Encode"/>.
    /// </summary>
    public static class WavCodec
    {
        public static Waveform Decode(byte[] wav)
        {
            if (wav == null || wav.Length < 44)
                throw new ArgumentException("Not a valid WAV: too short.");
            if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
                throw new ArgumentException("Not a valid WAV: missing RIFF header.");

            int channels = 1, sampleRate = 16000, bitsPerSample = 16, audioFormat = 1;
            int pos = 12; // skip RIFF chunk descriptor
            int dataOffset = -1, dataLength = 0;

            while (pos + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, pos, 4);
                int chunkSize = BitConverter.ToInt32(wav, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    audioFormat = BitConverter.ToInt16(wav, body);
                    channels = BitConverter.ToInt16(wav, body + 2);
                    sampleRate = BitConverter.ToInt32(wav, body + 4);
                    bitsPerSample = BitConverter.ToInt16(wav, body + 14);
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = chunkSize;
                }
                pos = body + chunkSize + (chunkSize & 1); // chunks are word-aligned
            }

            if (dataOffset < 0) throw new ArgumentException("WAV has no data chunk.");
            if (channels < 1) channels = 1;

            int bytesPerSample = bitsPerSample / 8;
            int frames = dataLength / (bytesPerSample * channels);
            var mono = new float[frames];

            for (int f = 0; f < frames; f++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    int idx = dataOffset + (f * channels + c) * bytesPerSample;
                    if (audioFormat == 3 && bitsPerSample == 32)
                        sum += BitConverter.ToSingle(wav, idx);
                    else // PCM 16-bit
                        sum += BitConverter.ToInt16(wav, idx) / 32768f;
                }
                mono[f] = sum / channels;
            }

            return new Waveform(mono, sampleRate);
        }

        public static byte[] Encode(Waveform wf)
        {
            int frames = wf.Samples.Length;
            int dataLen = frames * 2; // 16-bit mono
            var bytes = new byte[44 + dataLen];

            void PutStr(int at, string s) { for (int i = 0; i < s.Length; i++) bytes[at + i] = (byte)s[i]; }
            void PutI32(int at, int v) => Array.Copy(BitConverter.GetBytes(v), 0, bytes, at, 4);
            void PutI16(int at, short v) => Array.Copy(BitConverter.GetBytes(v), 0, bytes, at, 2);

            PutStr(0, "RIFF"); PutI32(4, 36 + dataLen); PutStr(8, "WAVE");
            PutStr(12, "fmt "); PutI32(16, 16); PutI16(20, 1); PutI16(22, 1);
            PutI32(24, wf.SampleRateHz); PutI32(28, wf.SampleRateHz * 2); PutI16(32, 2); PutI16(34, 16);
            PutStr(36, "data"); PutI32(40, dataLen);

            for (int i = 0; i < frames; i++)
            {
                short s = (short)Math.Round(Math.Max(-1f, Math.Min(1f, wf.Samples[i])) * 32767f);
                PutI16(44 + i * 2, s);
            }
            return bytes;
        }
    }
}
