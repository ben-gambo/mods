using System;
using System.Collections.Generic;

namespace Gambonanza.CashbackStrain
{
    // Replace individual callbacks, keeping their position in a multicast field.
    // In particular, never place cashback before another gambit's WIN effect.
    internal sealed class OrderedDelegateHooks<T>
    {
        private readonly Func<Action<T>, bool> _matches;
        private readonly Func<Action<T>, bool> _alive;
        private readonly Func<Action<T>, Action<T>> _wrap;
        private readonly Dictionary<Action<T>, Action<T>> _originals = new Dictionary<Action<T>, Action<T>>();
        private readonly Dictionary<Action<T>, Action<T>> _wrappers = new Dictionary<Action<T>, Action<T>>();
        private Action<T> _lastChain;

        internal OrderedDelegateHooks(Func<Action<T>, bool> matches,
            Func<Action<T>, bool> alive, Func<Action<T>, Action<T>> wrap)
        {
            _matches = matches;
            _alive = alive;
            _wrap = wrap;
        }

        internal Action<T> Reconcile(Action<T> chain)
        {
            if (ReferenceEquals(chain, _lastChain) && !HasDestroyedTarget()) return chain;

            var callbacks = new List<Action<T>>();
            var seen = new HashSet<Action<T>>();
            bool changed = false;
            if (chain != null)
            {
                foreach (Action<T> callback in chain.GetInvocationList())
                {
                    if (_wrappers.TryGetValue(callback, out var original))
                    {
                        // Native OnDestroy removes its original delegate. Once wrapped,
                        // that removal cannot find it, so we remove the dead wrapper here.
                        if (!_alive(original)) { changed = true; continue; }
                        seen.Add(original);
                        callbacks.Add(callback);
                    }
                    else if (_matches(callback) && _alive(callback))
                    {
                        if (!_originals.TryGetValue(callback, out var wrapper))
                        {
                            wrapper = _wrap(callback);
                            _originals.Add(callback, wrapper);
                            _wrappers.Add(wrapper, callback);
                        }
                        seen.Add(callback);
                        callbacks.Add(wrapper);
                        changed = true;
                    }
                    else callbacks.Add(callback);
                }
            }

            // A different mod may replace or remove callbacks. Do not resurrect them.
            var removed = new List<Action<T>>();
            foreach (var pair in _originals)
                if (!seen.Contains(pair.Key)) removed.Add(pair.Key);
            foreach (var original in removed)
            {
                _wrappers.Remove(_originals[original]);
                _originals.Remove(original);
            }

            _lastChain = changed ? Combine(callbacks) : chain;
            return _lastChain;
        }

        internal Action<T> Restore(Action<T> chain)
        {
            var callbacks = new List<Action<T>>();
            bool changed = false;
            if (chain != null)
            {
                foreach (Action<T> callback in chain.GetInvocationList())
                {
                    if (!_wrappers.TryGetValue(callback, out var original))
                    {
                        callbacks.Add(callback);
                        continue;
                    }
                    changed = true;
                    if (_alive(original)) callbacks.Add(original);
                }
            }
            _originals.Clear();
            _wrappers.Clear();
            _lastChain = null;
            return changed ? Combine(callbacks) : chain;
        }

        private bool HasDestroyedTarget()
        {
            foreach (var original in _originals.Keys)
                if (!_alive(original)) return true;
            return false;
        }

        private static Action<T> Combine(List<Action<T>> callbacks)
        {
            Action<T> result = null;
            foreach (var callback in callbacks) result += callback;
            return result;
        }
    }
}
