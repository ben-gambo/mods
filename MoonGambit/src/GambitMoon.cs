namespace Gambonanza.MoonGambit
{
    using Blukulele.CHE;

    /// <summary>
    /// The Moon Gambit's entire runtime behaviour.
    ///
    /// It subscribes to nothing, it modifies nothing, and Trigger() - which
    /// nothing ever calls - does nothing. The card's only mechanical property
    /// is being a "moon" in a gambit slot, which <see cref="MergeWatcher"/>
    /// looks for when something is dropped onto the vanilla Sun.
    /// </summary>
    public sealed class GambitMoon : BaseGambit
    {
        public override void Trigger()
        {
        }
    }
}
