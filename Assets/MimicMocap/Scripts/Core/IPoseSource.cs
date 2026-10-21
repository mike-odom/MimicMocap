using System;
using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// A pose estimator. Implementations are driven from the main thread:
    /// call <see cref="Submit"/> with a new camera frame when <see cref="IsBusy"/> is false,
    /// then poll <see cref="TryGetLatest"/> each frame for a completed result.
    /// </summary>
    public interface IPoseSource : IDisposable
    {
        string Name { get; }

        /// <summary>False when the source cannot run on this machine (missing DLL, no GPU support).</summary>
        bool IsAvailable { get; }

        /// <summary>True while a submitted frame is still being processed.</summary>
        bool IsBusy { get; }

        /// <summary>Human-readable status for the HUD (model variant, last error).</summary>
        string Status { get; }

        /// <summary>Load models and allocate resources. Sets <see cref="IsAvailable"/>.</summary>
        void Initialize();

        /// <summary>Start estimating a pose for this frame. Ignored while busy.</summary>
        void Submit(Texture frame, double timestamp);

        /// <summary>
        /// Copy the newest completed pose into <paramref name="frame"/>.
        /// Returns false if nothing new has completed since the last call.
        /// </summary>
        bool TryGetLatest(PoseFrame frame);
    }
}
