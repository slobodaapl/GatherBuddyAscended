using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GatherBuddy.AutoGather.Helpers;

public class SoundHelper
{
    private const string SoundResource = "GatherBuddy.CustomInfo.honk-sound.wav";

    public void StartHonkSoundTask(int repeatCount)
        => Task.Run(() => PlayHonkSound(repeatCount));

    private void PlayHonkSound(int repeatCount)
    {
        try
        {
            var       assembly       = Assembly.GetExecutingAssembly();
            using var resourceStream = assembly.GetManifestResourceStream(SoundResource);
            if (resourceStream == null)
                throw new FileNotFoundException($"Embedded resource {SoundResource} not found.");

            using var ms = new MemoryStream();
            resourceStream.CopyTo(ms);
            var soundData = ms.ToArray(); // Keep a copy of the sound's bytes

            for (int i = 0; i < repeatCount; i++)
            {
                using var audioStream = new MemoryStream(soundData); // Fresh each time
                using var soundSource = new WaveFileReader(audioStream);
                var volume = new VolumeSampleProvider(soundSource.ToSampleProvider())
                {
                    Volume = GatherBuddy.Config.AutoGatherConfig.SoundPlaybackVolume / 100f,
                };
                using var soundOut = new WasapiOut(AudioClientShareMode.Shared, true, 100);
                soundOut.Init(volume);

                soundOut.Play();
                while (soundOut.PlaybackState == PlaybackState.Playing)
                    Task.Delay(10).Wait();

                Task.Delay(200).Wait();
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"Error during honk: {ex}");
        }
    }

}
