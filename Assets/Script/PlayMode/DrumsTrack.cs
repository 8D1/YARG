using System.Collections.Generic;
using UnityEngine;
using YARG.Data;
using YARG.Input;
using YARG.Pools;
using YARG.Util;

namespace YARG.PlayMode {
	public class DrumsTrack : AbstractTrack {
		private DrumsInputStrategy input;

		[Space]
		[SerializeField]
		private Fret[] drums;
		[SerializeField]
		private Color[] drumColors;
		[SerializeField]
		private NotePool notePool;
		[SerializeField]
		private Pool genericPool;
		[SerializeField]
		private ParticleGroup kickNoteParticles;

		private int visualChartIndex = 0;
		private int realChartIndex = 0;
		private int eventChartIndex = 0;

		private Queue<List<NoteInfo>> expectedHits = new();

		private int notesHit = 0;

		protected override void StartTrack() {
			notePool.player = player;
			genericPool.player = player;

			// Inputs

			input = (DrumsInputStrategy) player.inputStrategy;
			input.ResetForSong();

			input.DrumHitEvent += DrumHitAction;

			// Color drums
			for (int i = 0; i < 4; i++) {
				var fret = drums[i].GetComponent<Fret>();
				fret.SetColor(drumColors[i]);
				drums[i] = fret;
			}
			kickNoteParticles.Colorize(drumColors[4]);
		}

		protected override void OnDestroy() {
			base.OnDestroy();

			// Unbind input
			input.DrumHitEvent -= DrumHitAction;

			// Set score
			player.lastScore = new PlayerManager.Score {
				percentage = notesHit == 0 ? 1f : (float) notesHit / Chart.Count,
				notesHit = notesHit,
				notesMissed = Chart.Count - notesHit
			};
		}

		protected override void UpdateTrack() {
			// Update input strategy
			if (input.botMode) {
				input.UpdateBotMode(Chart, Play.Instance.SongTime);
			} else {
				input.UpdatePlayerMode();
			}

			// Ignore everything else until the song starts
			if (!Play.Instance.SongStarted) {
				return;
			}

			var events = Play.Instance.chart.events;

			// Update events (beat lines, starpower, etc.)
			while (events.Count > eventChartIndex && events[eventChartIndex].time <= RelativeTime) {
				var eventInfo = events[eventChartIndex];

				float compensation = TRACK_SPAWN_OFFSET - CalcLagCompensation(RelativeTime, eventInfo.time);
				if (eventInfo.name == "beatLine_minor") {
					genericPool.Add("beatLine_minor", new(0f, 0.01f, compensation));
				} else if (eventInfo.name == "beatLine_major") {
					genericPool.Add("beatLine_major", new(0f, 0.01f, compensation));
				} else if (eventInfo.name == $"starpower_{player.chosenInstrument}") {
					StarpowerSection = eventInfo;
				}

				eventChartIndex++;
			}

			// Since chart is sorted, this is guaranteed to work
			while (Chart.Count > visualChartIndex && Chart[visualChartIndex].time <= RelativeTime) {
				var noteInfo = Chart[visualChartIndex];

				SpawnNote(noteInfo, RelativeTime);
				visualChartIndex++;
			}

			// Update expected input
			while (Chart.Count > realChartIndex && Chart[realChartIndex].time <= Play.Instance.SongTime + Play.HIT_MARGIN) {
				var noteInfo = Chart[realChartIndex];

				var peeked = expectedHits.ReversePeekOrNull();
				if (peeked?[0].time == noteInfo.time) {
					// Add notes as chords
					peeked.Add(noteInfo);
				} else {
					// Or add notes as singular
					var l = new List<NoteInfo>(5) { noteInfo };
					expectedHits.Enqueue(l);
				}

				realChartIndex++;
			}

			UpdateInput();
		}

		private void UpdateInput() {
			// Handle misses (multiple a frame in case of lag)
			while (Play.Instance.SongTime - expectedHits.PeekOrNull()?[0].time > Play.HIT_MARGIN) {
				var missedChord = expectedHits.Dequeue();

				// Call miss for each component
				Combo = 0;
				foreach (var hit in missedChord) {
					notePool.MissNote(hit);
					StopAudio = true;
				}
			}
		}

		private void DrumHitAction(int drum) {
			// Overstrum if no expected
			if (expectedHits.Count <= 0) {
				Combo = 0;

				return;
			}

			// Handle hits (one per frame so no double hits)
			var chord = expectedHits.Peek();

			// Check if a drum was hit
			NoteInfo hit = null;
			foreach (var note in chord) {
				if (note.fret == drum) {
					hit = note;
					break;
				}
			}

			// Overstrum (or overhit in this case)
			if (hit == null) {
				Combo = 0;

				return;
			}

			// If so, hit! (Remove from "chord")'
			bool lastNote = false;
			chord.RemoveAll(i => i.fret == drum);
			if (chord.Count <= 0) {
				lastNote = true;
				expectedHits.Dequeue();
			}

			if (lastNote) {
				Combo++;
			}

			// Hit note
			notePool.HitNote(hit);
			StopAudio = false;

			// Play particles
			if (hit.fret != 4) {
				drums[hit.fret].PlayParticles();
			} else {
				kickNoteParticles.Play();
			}

			// Add stats
			notesHit++;
		}

		private void SpawnNote(NoteInfo noteInfo, float time) {
			// Set correct position
			float lagCompensation = CalcLagCompensation(time, noteInfo.time);
			float x = noteInfo.fret == 4 ? 0f : drums[noteInfo.fret].transform.localPosition.x;
			var pos = new Vector3(x, 0f, TRACK_SPAWN_OFFSET - lagCompensation);

			// Get model type
			var model = NoteComponent.ModelType.NOTE;
			if (noteInfo.fret == 4) {
				model = NoteComponent.ModelType.FULL;
			} else if (noteInfo.hopo) {
				model = NoteComponent.ModelType.HOPO;
			}

			// Set note info
			var noteComp = notePool.AddNote(noteInfo, pos);
			noteComp.SetInfo(drumColors[noteInfo.fret], noteInfo.length, model);
		}
	}
}