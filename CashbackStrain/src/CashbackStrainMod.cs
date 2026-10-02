using Gambonanza.ModSdk;
using Gambonanza.StrainApi;

namespace Gambonanza.CashbackStrain
{
    public sealed class CashbackStrainMod : IMod, IModLifecycle
    {
        public const string StrainId = "cashback";
        private IModContext _context;

        public void OnLoad(IModContext context) => _context = context;

        public void OnEnable()
        {
            if (typeof(StrainBuilder).GetMethod("AsBonus", System.Type.EmptyTypes) == null)
            {
                _context?.LogLine("Cashback requires StrainApi 1.1.0 or newer. Update StrainApi, then restart the game.");
                return;
            }
            RegisterBonus();
        }

        // Keep the new API call out of OnEnable so older APIs can report the update
        // requirement before the runtime resolves AsBonus.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void RegisterBonus()
        {
            StrainBuilder.Create(StrainId)
                .WithName("Cashback")
                .WithDescription("Expiring gambits pay their <color=*>sell value</color>.")
                .AsBonus()
                .WithGameIcon("SPR_Lucky_Coin")
                .WithBehaviour<CashbackBehaviour>()
                .Register();

            _context?.LogLine("Cashback registered: pick it in the Custom run bonuses, or use 'strain on cashback'.");
        }

        public void OnDisable()
        {
            Strains.Unregister(StrainId);
        }
    }
}
