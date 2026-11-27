using CS2GameHelper.Core.Data;
using System.Numerics;
using CS2GameHelper.Data.Game;
using CS2GameHelper.Utils;

namespace CS2GameHelper.Data.Entity;

[Flags]
public enum EntityFlags
{
    None = 0,
    OnGround = 1,
    PartiallyInWater = 2,
    FullyInWater = 4,
    NoInterpolation = 8,
    OnFire = 16,
    StaticProp = 32
}

public abstract class EntityBase
{
    protected IntPtr EntityList { get; private set; }
    protected IntPtr ControllerBase { get; private set; }
    public IntPtr AddressBase { get; private set; }

    private bool LifeState { get; set; }
    public int Health { get; private set; }
    public Team Team { get; private set; }
    protected Vector3 Origin { get; private set; }
    public int ShotsFired { get; private set; }

    // === НОВЫЕ ПОЛЯ ===
    public Vector3 Position => Origin; // Position = Origin в CS2
    public int Armor { get; private set; }
    public bool HasHelmet { get; private set; }
    public bool IsVisible { get; private set; }
    public EntityFlags Flags { get; private set; }
    public Vector2? ViewAngle { get; private set; }
    public bool IsReloading { get; private set; }
    public bool IsDefusing { get; private set; }

    public IntPtr ObserverTarget { get; private set; }

    private IntPtr CurrentWeapon { get; set; }
    public string CurrentWeaponName { get; private set; } = null!;

    private int WeaponIndex { get; set; }
    public Vector3 Velocity { get; private set; }

    public virtual bool IsAlive()
    {
        return ControllerBase != IntPtr.Zero &&
               AddressBase != IntPtr.Zero &&
               LifeState &&
               Health > 0 &&
               Team is Team.Terrorists or Team.CounterTerrorists;
    }

    protected abstract IntPtr ReadControllerBase(GameProcess gameProcess);
    protected abstract IntPtr ReadAddressBase(GameProcess gameProcess);

    public virtual bool Update(GameProcess gameProcess)
    {
        if (gameProcess.ModuleClient == null)
            throw new ArgumentNullException(nameof(gameProcess.ModuleClient), "ModuleClient cannot be null.");
        EntityList = gameProcess.ModuleClient.Read<IntPtr>(Offsets.dwEntityList);
        ControllerBase = ReadControllerBase(gameProcess);
        AddressBase = ReadAddressBase(gameProcess);
        if (ControllerBase == IntPtr.Zero || AddressBase == IntPtr.Zero) return false;

        if (gameProcess.Process == null)
            throw new ArgumentNullException(nameof(gameProcess.Process), "Process cannot be null.");

        LifeState = gameProcess.Read<bool>(AddressBase + Offsets.m_lifeState);
        Health = gameProcess.Read<int>(AddressBase + Offsets.m_iHealth);
        Team = gameProcess.Read<int>(AddressBase + Offsets.m_iTeamNum).ToTeam();

        // The world position lives on the game scene node, not on the pawn.
        var sceneNode = gameProcess.Read<IntPtr>(AddressBase + Offsets.m_pGameSceneNode);
        Origin = ReadOrigin(gameProcess, sceneNode);

        ShotsFired = gameProcess.Read<int>(AddressBase + Offsets.m_iShotsFired);

        // === Читаем недостающие поля ===
        Armor = gameProcess.Read<int>(AddressBase + Offsets.m_ArmorValue);

        // Helmet / defuser live on the item-services object.
        var itemServices = gameProcess.Read<IntPtr>(AddressBase + Offsets.m_pItemServices);
        HasHelmet = itemServices != IntPtr.Zero &&
                    gameProcess.Read<bool>(itemServices + Offsets.m_bHasHelmet);

        // "Spotted" is a bit inside the embedded EntitySpottedState_t structure.
        IsVisible = gameProcess.Read<bool>(
            AddressBase + Offsets.m_entitySpottedState + Offsets.m_bSpotted);

        Flags = (EntityFlags)gameProcess.Read<int>(AddressBase + Offsets.m_fFlags);
        IsDefusing = gameProcess.Read<bool>(AddressBase + Offsets.m_bBeingDefused);

        // ViewAngle (углы камеры)
        try
        {
            var viewPunch = gameProcess.Read<Vector2>(AddressBase + Offsets.m_angEyeAngles);
            ViewAngle = viewPunch;
        }
        catch
        {
            ViewAngle = null;
        }

        // Оружие
        try
        {
            CurrentWeapon = ResolveActiveWeapon(gameProcess);
            if (CurrentWeapon != IntPtr.Zero)
            {
                // C_EconEntity embeds C_AttributeContainer, which in turn embeds
                // C_EconItemView, so the item definition index is a flat sum of offsets.
                var weaponIndexAddress = CurrentWeapon + Offsets.m_AttributeManager +
                                        Offsets.m_Item + Offsets.m_iItemDefinitionIndex;
                var rawIndex = gameProcess.Read<ushort>(weaponIndexAddress);
                WeaponIndex = rawIndex;

                IsReloading = gameProcess.Read<bool>(CurrentWeapon + Offsets.m_bInReload);

                var name = Enum.GetName(typeof(WeaponIndexes), WeaponIndex);
                CurrentWeaponName = string.IsNullOrEmpty(name) ? $"weapon_{WeaponIndex}" : name;
            }
            else
            {
                WeaponIndex = -1;
                IsReloading = false;
                CurrentWeaponName = string.Empty;
            }
        }
        catch
        {
            CurrentWeapon = IntPtr.Zero;
            WeaponIndex = -1;
            IsReloading = false;
            CurrentWeaponName = string.Empty;
        }

        Velocity = gameProcess.Read<Vector3>(AddressBase + Offsets.m_vecAbsVelocity);

        // Spectator target: handle -> entity address through the observer services.
        try
        {
            var observerServices = gameProcess.Read<IntPtr>(AddressBase + Offsets.m_pObserverServices);
            if (observerServices != IntPtr.Zero)
            {
                var observerHandle = gameProcess.Read<int>(observerServices + Offsets.m_hObserverTarget);
                ObserverTarget = ResolveEntityHandle(gameProcess, observerHandle);
            }
            else
            {
                ObserverTarget = IntPtr.Zero;
            }
        }
        catch
        {
            ObserverTarget = IntPtr.Zero;
        }


        return true;
    }

    /// <summary>
    /// Reads the world position from the game scene node. CS2 keeps the origin
    /// on CGameSceneNode; the pawn itself only carries a stale "old origin".
    /// </summary>
    private static Vector3 ReadOrigin(GameProcess gameProcess, IntPtr sceneNode)
    {
        if (sceneNode == IntPtr.Zero) return Vector3.Zero;

        var origin = gameProcess.Read<Vector3>(sceneNode + Offsets.m_vecOrigin);
        if (origin.LengthSquared() > 0.01f) return origin;

        // Fall back to the absolute origin when the quantized origin is empty.
        var absOrigin = gameProcess.Read<Vector3>(sceneNode + Offsets.m_vecAbsOrigin);
        return absOrigin;
    }

    /// <summary>
    /// Resolves the pawn's active weapon: pawn -> weapon services -> active
    /// weapon handle -> entity-list entry.
    /// </summary>
    private IntPtr ResolveActiveWeapon(GameProcess gameProcess)
    {
        var weaponServices = gameProcess.Read<IntPtr>(AddressBase + Offsets.m_pWeaponServices);
        if (weaponServices == IntPtr.Zero) return IntPtr.Zero;

        var handle = gameProcess.Read<int>(weaponServices + Offsets.m_hActiveWeapon);
        return ResolveEntityHandle(gameProcess, handle);
    }

    /// <summary>
    /// Converts an entity handle (handle &amp; 0x7FFF = entity index) into the
    /// absolute address of that entity inside the client entity list.
    /// </summary>
    protected IntPtr ResolveEntityHandle(GameProcess gameProcess, int handle)
    {
        if (handle == 0 || handle == -1) return IntPtr.Zero;
        if (EntityList == IntPtr.Zero) return IntPtr.Zero;

        var index = handle & 0x7FFF;
        if (index <= 0) return IntPtr.Zero;

        var listEntry = gameProcess.Read<IntPtr>(EntityList + 8 * (index >> 9) + 16);
        if (listEntry == IntPtr.Zero) return IntPtr.Zero;

        return gameProcess.Read<IntPtr>(listEntry + 112 * (index & 0x1FF));
    }


}