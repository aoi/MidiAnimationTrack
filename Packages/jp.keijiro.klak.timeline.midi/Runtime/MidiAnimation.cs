using UnityEngine;
using UnityEngine.Playables;

namespace Klak.Timeline.Midi
{
    // Runtime playable class that calculates MIDI based animation
    [System.Serializable]
    public sealed class MidiAnimation : PlayableBehaviour
    {
        #region Serialized variables

        public float tempo = 120;
        public uint duration;
        public uint ticksPerQuarterNote = 96;
        public MidiEvent [] events;


        float timeCache = 0f;
        int idx = 0;
        uint previousTick = 0;

        #endregion

        #region Public properties and methods

        public float DurationInSecond {
            get { return duration / tempo * 60 / ticksPerQuarterNote; }
        }

        public float GetValue(Playable playable, MidiControl control)
        {
            if (events == null) return 0;
            var t = (float)playable.GetTime() % DurationInSecond;
            if (control.mode == MidiControl.Mode.NoteEnvelope)
                return GetNoteEnvelopeValue(control, t);
            else if (control.mode == MidiControl.Mode.NoteCurve)
                return GetNoteCurveValue(control, t);
            else // CC
                return GetCCValue(control, t);
        }

        #endregion

        #region PlayableBehaviour implementation

        float _previousTime;

        public override void OnGraphStart(Playable playable)
        {
            _previousTime = (float)playable.GetTime();

            timeCache = 0f;
            idx = 0;
            previousTick = 0;

        }

        public override void OnBehaviourPause(Playable playable, FrameData info)
        {
            // When the playable is being finished, signals laying in the rest
            // of the clip should be all triggered.
            if (!playable.IsDone()) return;
            var duration = (float)playable.GetDuration();
            var t = ConvertSecondToTicks(duration);
            TriggerSignals(playable, info.output, previousTick, t);
        }

        public override void PrepareFrame(Playable playable, FrameData info)
        {
            var current = (float)playable.GetTime();

            var currentTick = ConvertSecondToTicksWithCache(current);

            // Playback or scrubbing?
            if (info.evaluationType == FrameData.EvaluationType.Playback)
            {
                // Trigger signals between the prrevious/current time.
                TriggerSignals(playable, info.output, previousTick, currentTick);
            }
            else
            {
                // Maximum allowable time difference for scrubbing
                const float maxDiff = 0.1f;

                // If the time is increasing and the difference is smaller
                // than maxDiff, it's being scrubbed.
                if (current - _previousTime < maxDiff)
                {
                    // Trigger the signals as usual.
                    TriggerSignals(playable, info.output, previousTick, currentTick);
                }
                else
                {
                    // It's jumping not scrubbed, so trigger signals laying
                    // around the current frame.
                    var t0 = Mathf.Max(0, current - maxDiff);
                    var t = ConvertSecondToTicks(t0);
                    TriggerSignals(playable, info.output, t, currentTick);
                }
            }

            _previousTime = current;
            previousTick = currentTick;
        }

        #endregion

        #region MIDI signal emission

        MidiSignalPool _signalPool = new MidiSignalPool();

        void TriggerSignals
            (Playable playable, PlayableOutput output, uint previous, uint current)
        {
            _signalPool.ResetFrame();

            // var t0 = ConvertSecondToTicksWithCache(previous);
            // var t0 = previousTick;
            // var t1 = ConvertSecondToTicksWithCache(current);
            var t0 = previous;
            var t1 = current;

            // Resolve wrapping-around cases by offsetting.
            if (t1 < t0) t1 += (t0 / duration + 1) * duration;

            // Offset both the points to make t0 < duration.
            var offs = (t0 / duration) * duration;
            t0 -= offs;
            t1 -= offs;

            // Resolve loops.
            for (; t1 >= duration; t1 -= duration)
            {
                // Trigger signals between t0 and the end of the clip.
                TriggerSignalsTick(playable, output, t0, 0xffffffffu);
                t0 = 0;
            }

            // Trigger signals between t0 and t1.
            TriggerSignalsTick(playable, output, t0, t1);

            previousTick = t1;
        }

        void TriggerSignalsTick
            (Playable playable, PlayableOutput output, uint previous, uint current)
        {
            foreach (var e in events)
            {
                if (e.tick >= current) break;
                if (e.tick < previous) continue;
                
                if (e.IsTempoSet) {
                    tempo = e.data2;
                    continue;
                }
                if (!e.IsNote) continue;
                output.PushNotification(playable, _signalPool.Allocate(e));
            }
        }

        #endregion

        #region Private variables and methods

        (int i0, int i1) GetCCEventIndexAroundTick(uint tick, int ccNumber)
        {
            var last = -1;
            for (var i = 0; i < events.Length; i++)
            {
                ref var e = ref events[i];
                if (!e.IsCC || e.data1 != ccNumber) continue;
                if (e.tick > tick) return (last, i);
                last = i;
            }
            return (last, last);
        }

        (int iOn, int iOff) GetNoteEventsBeforeTick(uint tick, MidiNoteFilter note)
        {
            var iOn = -1;
            var iOff = -1;
            for (var i = 0; i < events.Length; i++)
            {
                ref var e = ref events[i];
                if (e.tick > tick) break;
                if (!note.Check(e)) continue;
                if (e.IsNoteOn) iOn = i; else iOff = i;
            }
            return (iOn, iOff);
        }

        uint ConvertSecondToTicks(float time)
        {
            // return (uint)(time * tempo / 60 * ticksPerQuarterNote);

            uint tick = 0;
            for (var i = 0; i < events.Length; i++) {

                if (time <= events[i].time) {
                    break;
                }

                tick = events[i].tick;
            }

            return tick;
        }

        uint ConvertSecondToTicksWithCache(float time)
        {
            // return (uint)(time * tempo / 60 * ticksPerQuarterNote);
            var iTmp = 0;
            if (timeCache <= time) {
                iTmp = idx;
            }
            uint tick = 0;
            for (var i = iTmp; i < events.Length; i++) {

                if (time <= events[i].time) {
                    break;
                }

                tick = events[i].tick;
            }

            return tick;
        }

        float ConvertTicksToSecond(uint tick)
        {
            return tick * 60 / (tempo * ticksPerQuarterNote);
        }

        #endregion

        #region Envelope generator

        float CalculateEnvelope(MidiEnvelope envelope, float onTime, float offTime)
        {
            var attackTime = envelope.AttackTime;
            var attackRate = 1 / attackTime;

            var decayTime = envelope.DecayTime;
            var decayRate = 1 / decayTime;

            var level = -offTime / envelope.ReleaseTime;

            if (onTime < attackTime)
            {
                level += onTime * attackRate;
            }
            else if (onTime < attackTime + decayTime)
            {
                level += 1 - (onTime - attackTime) * decayRate * (1 - envelope.SustainLevel);
            }
            else
            {
                level += envelope.SustainLevel;
            }

            return Mathf.Max(0, level);
        }

        #endregion

        #region Value calculation methods

        float GetNoteEnvelopeValue(MidiControl control, float time)
        {
            var tick = ConvertSecondToTicks(time);
            var pair = GetNoteEventsBeforeTick(tick, control.noteFilter);

            if (pair.iOn < 0) return 0;
            ref var eOn = ref events[pair.iOn]; // Note-on event

            // Note-on time
            var onTime = ConvertTicksToSecond(eOn.tick);

            // Note-off time
            var offTime = pair.iOff < 0 || pair.iOff < pair.iOn ?
                time : ConvertTicksToSecond(events[pair.iOff].tick);

            var envelope = CalculateEnvelope(
                control.envelope,
                Mathf.Max(0, offTime - onTime),
                Mathf.Max(0, time - offTime)
            );

            var velocity = eOn.data2 / 127.0f;

            return envelope * velocity;
        }

        float GetNoteCurveValue(MidiControl control, float time)
        {
            var tick = ConvertSecondToTicks(time);
            var pair = GetNoteEventsBeforeTick(tick, control.noteFilter);

            if (pair.iOn < 0) return 0;
            ref var eOn = ref events[pair.iOn]; // Note-on event

            // Note-on time
            var onTime = ConvertTicksToSecond(eOn.tick);

            var curve = control.curve.Evaluate(Mathf.Max(0, time - onTime));
            var velocity = eOn.data2 / 127.0f;

            return curve * velocity;
        }

        float GetCCValue(MidiControl control, float time)
        {
            var tick = ConvertSecondToTicks(time);
            var pair = GetCCEventIndexAroundTick(tick, control.ccNumber);

            if (pair.i0 < 0) return 0;
            if (pair.i1 < 0) return events[pair.i0].data2 / 127.0f;

            ref var e0 = ref events[pair.i0];
            ref var e1 = ref events[pair.i1];

            var t0 = ConvertTicksToSecond(e0.tick);
            var t1 = ConvertTicksToSecond(e1.tick);

            var v0 = e0.data2 / 127.0f;
            var v1 = e1.data2 / 127.0f;

            return Mathf.Lerp(v0, v1, Mathf.Clamp01((time - t0) / (t1 - t0)));
        }

        #endregion
    }
}
