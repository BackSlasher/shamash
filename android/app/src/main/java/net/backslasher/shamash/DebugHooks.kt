package net.backslasher.shamash

/** Process-wide handles the debug-only broadcast receivers use to reach the live controller. */
object DebugHooks {
    /** Set by MainActivity while it exists; call on the main thread. */
    @Volatile var speak: ((String) -> Unit)? = null
}
