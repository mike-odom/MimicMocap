using UnityEngine;

namespace MimicMocap
{
    /// <summary>
    /// Marks one switchable character under the director's CharacterRoot. The director keeps exactly one slot
    /// active at a time and binds a <see cref="HumanoidRetargeter"/> to its Animator the first time it is shown.
    /// </summary>
    public sealed class CharacterSlot : MonoBehaviour
    {
        [Tooltip("Name shown on the hand menu button.")]
        public string displayName = "";

        [Range(0.3f, 1.5f)]
        [Tooltip("Scale applied to the rig's Head bone. Stylised rigs with giant skulls hide the arms; 0.6 keeps gestures readable.")]
        public float headScale = 1f;

        public string DisplayName => string.IsNullOrEmpty(displayName) ? name : displayName;

        /// <summary>The rig's Animator (searched lazily; works while the object is inactive).</summary>
        public Animator Animator => m_Animator != null ? m_Animator : m_Animator = GetComponentInChildren<Animator>(true);
        Animator m_Animator;
    }
}
