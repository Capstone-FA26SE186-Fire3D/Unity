using Fire3D.Unity.Content;
using UnityEngine;

namespace Fire3D.Unity.Presentation
{
    /// <summary>
    /// Plays gameplay sound effects from <see cref="RuntimeAssets.SoundSet"/>: positional one-shots, looping sources
    /// on an object and flat UI sounds. Missing clips are skipped silently.
    /// </summary>
    public static class GameAudio
    {
        private static AudioSource _ui;

        public static RuntimeAssets.SoundSet Sounds { get; private set; }

        public static void Bind(RuntimeAssets assets) => Sounds = assets != null ? assets.Sounds : null;

        /// <summary>One-shot at a world position; <paramref name="maxSeconds"/> cuts a long recording short.</summary>
        public static void PlayAt(AudioClip clip, Vector3 position, float volume = 1f, float maxSeconds = 0f, float maxDistance = 18f)
        {
            if (clip == null)
                return;
            var go = new GameObject("Sfx_" + clip.name);
            go.transform.position = position;
            var source = go.AddComponent<AudioSource>();
            Configure3D(source, maxDistance);
            source.clip = clip;
            source.volume = volume;
            source.pitch = Random.Range(0.95f, 1.05f);
            source.Play();
            var life = maxSeconds > 0f ? Mathf.Min(maxSeconds, clip.length) : clip.length;
            Object.Destroy(go, life + 0.05f);
        }

        /// <summary>Adds a looping 3D source to <paramref name="host"/>; the caller plays, stops or fades it.</summary>
        public static AudioSource Loop(AudioClip clip, GameObject host, float volume, float maxDistance = 14f)
        {
            if (clip == null || host == null)
                return null;
            var source = host.AddComponent<AudioSource>();
            Configure3D(source, maxDistance);
            source.clip = clip;
            source.loop = true;
            source.volume = volume;
            // Start loops at a random point so several sources of one clip do not phase.
            source.time = Random.Range(0f, clip.length * 0.9f);
            return source;
        }

        /// <summary>Flat (non-positional) sound for HUD feedback.</summary>
        public static void Ui(AudioClip clip, float volume = 0.55f)
        {
            if (clip == null)
                return;
            if (_ui == null)
            {
                var go = new GameObject("GameAudioUi");
                Object.DontDestroyOnLoad(go);
                _ui = go.AddComponent<AudioSource>();
                _ui.playOnAwake = false;
                _ui.spatialBlend = 0f;
            }

            _ui.PlayOneShot(clip, volume);
        }

        private static void Configure3D(AudioSource source, float maxDistance)
        {
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 1f;
            source.maxDistance = maxDistance;
            source.dopplerLevel = 0f;
        }
    }
}
