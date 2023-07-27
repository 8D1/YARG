using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core;
using YARG.Core.Chart;
using YARG.Core.Replays.IO;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;
using YARG.Input;
using YARG.Player;
using YARG.Replays;
using YARG.Settings;
using YARG.Song;

namespace YARG.Gameplay
{
    public class GameManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        private TrackViewManager _trackViewManager;

        [Header("Instrument Prefabs")]
        [SerializeField]
        private GameObject fiveFretGuitarPrefab;

        [SerializeField]
        private GameObject sixFretGuitarPrefab;

        [SerializeField]
        private GameObject fourLaneDrumsPrefab;

        [SerializeField]
        private GameObject fiveLaneDrumsPrefab;

        [SerializeField]
        private GameObject proGuitarPrefab;

        private const float SONG_START_DELAY = 2f;

        // All access to chart data must be done through this event,
        // since things are loaded asynchronously
        // Players are initialized by hand and don't go through this event
        public event Action<SongChart> ChartLoaded;

        public SongEntry Song  { get; private set; }

        public double SongStartTime { get; private set; }
        public double SongLength    { get; private set; }

        public double AudioCalibration => -SettingsManager.Settings.AudioCalibration.Data / 1000.0;

        /// <summary>
        /// The time into the song <b>without</b> accounting for calibration.<br/>
        /// This is updated every frame.
        /// </summary>
        public double RealSongTime { get; private set; }
        /// <summary>
        /// The time into the song <b>accounting</b> for calibration.<br/>
        /// This is updated every frame.
        /// </summary>
        public double SongTime => RealSongTime + AudioCalibration;

        public bool IsReplay { get; private set; }

        public bool Paused { get; private set; }

        public int BandScore { get; private set; }
        public int BandCombo { get; private set; }

        private SongChart _chart;

        private List<BasePlayer> _players;
        private List<Beatline>   _beats;

        public IReadOnlyList<BasePlayer> Players => _players;

        private UniTask _syncAudio = UniTask.CompletedTask;

        private void Awake()
        {
            Song = GlobalVariables.Instance.CurrentSong;
            IsReplay = GlobalVariables.Instance.IsReplay;
        }

        private async UniTask Start()
        {
            // Disable until everything's loaded
            enabled = false;

            // Load chart/audio
            LoadingManager.Instance.Queue(LoadChart, "Loading chart...");
            LoadingManager.Instance.Queue(LoadAudio, "Loading audio...");
            await LoadingManager.Instance.StartLoad();

            // Spawn players
            CreatePlayers();

            // Set time offsets
            RealSongTime = -SONG_START_DELAY;
            InputManager.InputTimeOffset = InputManager.CurrentInputTime
                - AudioCalibration // Subtract audio calibration so that times are adjusted for it
                + SONG_START_DELAY; // Add delay so that times before the audio actually starts are negative

            // Loaded, enable updates
            enabled = true;
        }

        private void Update()
        {
            // Calculate song time
            if (RealSongTime < 0.0)
            {
                // Drive song time using input time until it's time to start the audio
                RealSongTime = InputManager.RelativeUpdateTime;
                if (RealSongTime >= 0.0)
                {
                    // Start audio
                    GlobalVariables.AudioManager.Play();
                    // Seek to calculated time to keep everything in sync
                    GlobalVariables.AudioManager.SetPosition(RealSongTime);
                }
            }
            else
            {
                RealSongTime = GlobalVariables.AudioManager.CurrentPositionD;
            }

            if (_syncAudio.Status != UniTaskStatus.Pending)
                _syncAudio = SyncAudio();

            if (Paused)
            {
                return;
            }

            int totalScore = 0;
            int totalCombo = 0;
            foreach (var player in _players)
            {
                player.UpdateWithTimes(InputManager.RelativeUpdateTime, SongTime);

                totalScore += player.Score;
                totalCombo += player.Combo;
            }

            BandScore = totalScore;
        }

        private async UniTask SyncAudio()
        {
            const double SyncThreshold = 0.025;
            double inputTime = InputManager.RelativeUpdateTime;
            double audioTime = SongTime;

            if (audioTime < 0.0)
                return;

            double delta = inputTime - audioTime;
            if (Math.Abs(delta) < SyncThreshold)
                return;

            Debug.Log($"Resyncing audio position. Input: {inputTime}, audio: {audioTime}, delta: {delta}");

            float speed = delta > 0.0 ? 1.05f : 0.95f;
            GlobalVariables.AudioManager.SetSpeed(speed);

            await UniTask.WaitUntil(() =>
            {
                double newDelta = InputManager.RelativeUpdateTime - GlobalVariables.AudioManager.CurrentPositionD;
                return Math.Abs(newDelta) < SyncThreshold ||
                    // Detect overshooting
                    (delta > 0.0 && newDelta < 0.0) ||
                    (delta < 0.0 && newDelta > 0.0);
            });
            GlobalVariables.AudioManager.SetSpeed(1f);

            inputTime = InputManager.RelativeUpdateTime;
            audioTime = GlobalVariables.AudioManager.CurrentPositionD;
            double finalDelta = inputTime - audioTime;
            Debug.Log($"Audio synced. Input: {inputTime}, audio: {audioTime}, delta: {finalDelta}");
        }

        private async UniTask LoadChart()
        {
            await UniTask.RunOnThreadPool(() =>
            {
                string notesFile = Path.Combine(Song.Location, Song.NotesFile);
                Debug.Log(notesFile);
                _chart = SongChart.FromFile(new SongMetadata(), notesFile);

                var syncTrack = _chart.SyncTrack;
                if (syncTrack.Beatlines is null or { Count: < 1 })
                    _chart.SyncTrack.GenerateBeatlines(_chart.GetLastTick());

                _beats = _chart.SyncTrack.Beatlines;
            });

            ChartLoaded?.Invoke(_chart);
        }

        private async UniTask LoadAudio()
        {
            await UniTask.RunOnThreadPool(() =>
            {
                Song.LoadAudio(GlobalVariables.AudioManager, GlobalVariables.Instance.SongSpeed);
                SongLength = GlobalVariables.AudioManager.AudioLengthD;
                GlobalVariables.AudioManager.SongEnd += EndSong;
            });
        }

        private void CreatePlayers()
        {
            _players = new List<BasePlayer>();

            int count = -1;
            foreach (var player in PlayerContainer.Players)
            {
                count++;
                var prefab = player.Profile.GameMode switch
                {
                    GameMode.FiveFretGuitar => fiveFretGuitarPrefab,
                    GameMode.SixFretGuitar  => sixFretGuitarPrefab,
                    GameMode.FourLaneDrums  => fourLaneDrumsPrefab,
                    GameMode.FiveLaneDrums  => fiveLaneDrumsPrefab,
                    GameMode.ProGuitar      => proGuitarPrefab,

                    _ => null
                };
                if (prefab == null)
                {
                    continue;
                }

                var playerObject = Instantiate(prefab, new Vector3(count * 25f, 100f, 0f), prefab.transform.rotation);

                // Setup player
                var basePlayer = playerObject.GetComponent<BasePlayer>();
                basePlayer.Initialize(player, _chart);
                _players.Add(basePlayer);

                _trackViewManager.CreateTrackView(basePlayer);
            }
        }

        public void SetPaused(bool paused)
        {
            // Set pause menu gameobject active status

            if (paused)
            {
                GlobalVariables.AudioManager.Pause();
            }
            else
            {
                GlobalVariables.AudioManager.Play();
            }
        }

        private void EndSong()
        {
            GlobalVariables.AudioManager.SongEnd -= EndSong;

            if (!IsReplay)
            {
                var replay = ReplayContainer.CreateNewReplay(Song, _players);
                var entry = new ReplayEntry
                {
                    SongName = replay.SongName,
                    ArtistName = replay.ArtistName,
                    CharterName = replay.CharterName,
                    BandScore = replay.BandScore,
                    Date = replay.Date,
                    SongChecksum = replay.SongChecksum,
                    PlayerCount = replay.PlayerCount,
                    PlayerNames = replay.PlayerNames,
                    GameVersion = replay.Header.GameVersion,
                };

                entry.ReplayFile = entry.GetReplayName();

                ReplayIO.WriteReplay(Path.Combine(ReplayContainer.ReplayDirectory, entry.ReplayFile), replay);

                Debug.Log("Wrote replay");
            }
        }
    }
}