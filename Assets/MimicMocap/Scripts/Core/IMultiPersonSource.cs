namespace MimicMocap
{
    /// <summary>
    /// A pose source that can report several people from one frame. People are exposed through stable
    /// "slots": a person keeps their slot while the tracker keeps their id, so slot 0 can drive one avatar
    /// and slot 1 another without the two swapping when both are in view.
    /// </summary>
    public interface IMultiPersonSource
    {
        /// <summary>Number of slots available (the batch size the source was configured for).</summary>
        int SlotCount { get; }

        /// <summary>People detected in the latest frame.</summary>
        int PeopleCount { get; }

        /// <summary>True while the source is actually running in multi-person mode (the SDK may refuse it).</summary>
        bool MultiPersonActive { get; }

        /// <summary>
        /// Copy the latest pose for <paramref name="slot"/> into <paramref name="frame"/> (Tracked is false for an
        /// empty slot). Returns false if no new frame has completed since the last call for that slot.
        /// </summary>
        bool TryGetPerson(int slot, PoseFrame frame);

        /// <summary>The tracker id occupying a slot, or -1.</summary>
        int SlotId(int slot);
    }
}
