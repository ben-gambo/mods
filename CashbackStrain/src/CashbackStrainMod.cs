using Gambonanza.ModSdk;
using UnityEngine;

namespace Gambonanza.CashbackStrain
{
    public sealed class CashbackStrainMod : IMod, IModLifecycle
    {
        private IModContext _context;
        private GameObject _root;
        private CashbackBehaviour _runner;

        public void OnLoad(IModContext context) => _context = context;

        public void OnEnable()
        {
            if (_runner) return;
            GameObject root = null;
            try
            {
                root = new GameObject("__Cashback");
                root.hideFlags = HideFlags.HideAndDontSave;
                root.SetActive(false); // Bind logging before Awake/OnEnable run.
                var runner = root.AddComponent<CashbackBehaviour>();
                runner.Bind(_context);
                Object.DontDestroyOnLoad(root);
                _root = root;
                _runner = runner;
                root.SetActive(true);
                _context?.LogLine("enabled: expiring gambits pay their sell value on every difficulty.");
            }
            catch (System.Exception ex)
            {
                if (_runner) _runner.TearDown();
                if (root)
                {
                    root.SetActive(false);
                    Object.Destroy(root);
                }
                _root = null;
                _runner = null;
                _context?.LogLine("could not enable Cashback: " + ex);
            }
        }

        public void OnDisable()
        {
            if (_runner) _runner.TearDown();
            if (_root)
            {
                _root.SetActive(false);
                Object.Destroy(_root);
            }
            _runner = null;
            _root = null;
            _context?.LogLine("disabled.");
        }
    }
}
