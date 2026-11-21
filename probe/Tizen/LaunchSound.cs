using System;
using System.IO;
using Tizen.Applications;
using Tizen.Multimedia;

namespace Ru7100Probe
{
    /// The launch intro's sound (res/launch.wav, an original synthesized sting), played once on a cold start through
    /// the platform's WAV player on the media stream. Fire-and-forget: a TV that refuses it just stays silent.
    internal static class LaunchSound
    {
        public static void Play()
        {
            try
            {
                var path = Path.Combine(Application.Current.DirectoryInfo.SharedResource, "launch.wav");
                if (!File.Exists(path))
                    return;
                var policy = new AudioStreamPolicy(AudioStreamType.Media);
                WavPlayer.StartAsync(path, policy).ContinueWith(t =>
                {
                    policy.Dispose();
                    if (t.Exception != null)
                        Console.WriteLine("launch sound: " + t.Exception.GetBaseException().Message);
                });
            }
            catch (Exception error)
            {
                Console.WriteLine("launch sound: " + error.Message);
            }
        }
    }
}
