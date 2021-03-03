using System;
using System.Collections.Generic;
using UnityEngine;

namespace Klak.Timeline.Midi
{
    // SMF file deserializer implementation
    static class MidiFileDeserializer
    {
        #region Public members

        public static MidiFileAsset Load(byte [] data)
        {
            var reader = new MidiDataStreamReader(data);

            // Chunk type
            if (reader.ReadChars(4) != "MThd")
                throw new FormatException("Can't find header chunk.");
            
            // Chunk length
            if (reader.ReadBEUInt32() != 6u)
                throw new FormatException("Length of header chunk must be 6.");
            
            // Format (unused)
            reader.Advance(2);
            
            // Number of tracks
            var trackCount = reader.ReadBEUInt16();

            // Ticks per quarter note
            var tpqn = reader.ReadBEUInt16();
            if ((tpqn & 0x8000u) != 0)
                throw new FormatException ("SMPTE time code is not supported.");

            // Tracks
            var tracks = new MidiAnimationAsset [trackCount];
            for (var i = 0; i < trackCount; i++)
                tracks[i] = ReadTrack(reader, tpqn);

            // Asset instantiation
            var asset = ScriptableObject.CreateInstance<MidiFileAsset>();
            asset.tracks = tracks;
            return asset;
        }

        #endregion

        #region Private members
        
        static MidiAnimationAsset ReadTrack(MidiDataStreamReader reader, uint tpqn)
        {
            // Chunk type
            if (reader.ReadChars(4) != "MTrk")
                throw new FormatException ("Can't find track chunk.");
            
            // Chunk length
            var chunkEnd = reader.ReadBEUInt32();
            chunkEnd += reader.Position;

            // MIDI event sequence
            var events = new List<MidiEvent>();
            var tick = 0u;
            var time = 0f;
            var tempo = 120f;
            var stat = (byte)0;
            var ticksStr = "";
            var hitPointsCnt = 0;

            while (reader.Position < chunkEnd)
            {
                // Delta time
                var delta = reader.ReadMultiByteValue();
                tick += delta;
                // Time with tempo
                var secondsPerBeat = 60f / tempo;
                var deltaSeconds = secondsPerBeat * ((float)delta / (float)tpqn);
                time += deltaSeconds;

                // Status byte
                if ((reader.PeekByte() & 0x80u) != 0)
                    stat = reader.ReadByte();
                
                if (stat == 0xffu)
                {
                    // 0xff: Meta event
                    var meta = reader.ReadByte();

                    if (meta == 0x51u) { // tempo set
                        var dataLength = reader.ReadMultiByteValue();
                        var data = reader.ReadBEUInt24();
                        var tmp = Math.Round(60000000 / (float)data);
                        Debug.LogFormat("tick: {0}, tempo: {1}", tick, tmp);
                        tempo = (float)tmp;
                        events.Add(new MidiEvent {
                            time = time,
                            tick = tick,
                            status = stat,
                            data1 = meta,
                            data2 = new byte(), // dummy
                            tempo = tempo
                        });

                    } else {
                        reader.Advance(reader.ReadMultiByteValue());
                        events.Add(new MidiEvent {
                            time = time,
                            tick = tick,
                            status = stat,
                            data1 = meta,
                            data2 = new byte() // dummy
                        });
                        
                    }
                }
                else if (stat == 0xf0u)
                {
                    // 0xf0: SysEx (unused)
                    while (reader.ReadByte() != 0xf7u) {}
                    events.Add(new MidiEvent {
                        time = time,
                        tick = tick,
                        status = stat,
                        data1 = new byte(), // dummy
                        data2 = new byte() // dummy
                    });

                }
                else
                {
                    // MIDI event
                    var b1 = reader.ReadByte();
                    var b2 = (stat & 0xe0u) == 0xc0u ? (byte)0 : reader.ReadByte();
                    if (!Contains(events, tick, stat)) {
                        events.Add(new MidiEvent {
                            time = time, tick = tick, status = stat, data1 = b1, data2 = b2
                        });

                        if ((stat & 0xf0) == 0x90) {
                            Debug.LogFormat("tick: {0}, time: {1}, frame: {2}, note: {3}", (float)tick, time, Math.Round(time * 60), b1);
                            ticksStr += tick + "\n";

                            if (b1 == 0x26 || b1 == 0x23) {
                                hitPointsCnt++;
                            }

                        }
                    }
                }
            }
            // Debug.Log(ticksStr);
            Debug.LogFormat("HitPoints {0}", hitPointsCnt);

            // Quantize duration with bars.
            var bars = (tick + tpqn * 4 - 1) / (tpqn * 4);

            // Asset instantiation
            var asset = ScriptableObject.CreateInstance<MidiAnimationAsset>();
            asset.template.tempo = 120;
            asset.template.duration = bars * tpqn * 4;
            asset.template.ticksPerQuarterNote = tpqn;
            asset.template.events = events.ToArray();
            return asset;
        }

        static bool Contains(List<MidiEvent> events, float tick, byte status)
        {
            bool contains = false;
            foreach (var e in events) {
                if (e.tick == tick && e.status == status)
                {
                    contains = true;
                    break;
                }
            }

            return contains;
        }

        #endregion
    }
}
