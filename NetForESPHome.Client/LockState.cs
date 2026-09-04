namespace NetForESPHome.Client;

/// <summary>
/// Represents the state of a lock entity.
/// This corrects the bug from the aioesphomeapi Python library where Unlocked and Jammed had the same value.
/// </summary>
public enum LockState
{
    None = 0,
    Locked = 1,
    Unlocked = 2, 
    Jammed = 3,
    Locking = 4,
    Unlocking = 5,
}