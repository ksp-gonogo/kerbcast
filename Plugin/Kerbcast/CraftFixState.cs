namespace Kerbcast
{
    /* What a fix source holds about one craft. Only Positioned carries a
       position; every other state is a craft the camera may name but must
       not aim at, range or frame. */
    internal enum CraftFixState
    {
        /* The source has no such craft. */
        Unknown,
        /* The source knows the craft and declines to place it. */
        Refused,
        /* Positioned at the UT asked for. */
        Positioned,
        /* The source knows the craft but cannot place it. */
        Listed,
    }
}
