namespace Klak.Timeline.Midi
{
    // MIDI event raw data struct
    [System.Serializable]
    public struct MidiEvent
    {
        public float time;
        public uint tick;
        public byte status;
        public byte data1;
        public byte data2;

        public bool IsCC      { get { return (status & 0xb0) == 0xb0; } }
        public bool IsNote    { get { return (status & 0xe0) == 0x80; } }
        public bool IsNoteOn  { get { return (status & 0xf0) == 0x90; } }
        public bool IsNoteOff { get { return (status & 0xf0) == 0x80; } }
        public bool IsMeta    { get { return status == 0xff; } }
        public bool IsTempoSet { get { return (IsMeta && data1 == 0x51); } }

        public override string ToString()
        {
            return string.Format("[{0}: {1:X}, {2}, {3}]", tick, status, data1, data2);
        }
    }
}
