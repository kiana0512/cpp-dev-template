#include "ExpressionReceiverComponent.h"

#include "Async/Async.h"
#include "Components/SkeletalMeshComponent.h"
#include "Dom/JsonObject.h"
#include "Engine/SkeletalMesh.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "SocketSubsystem.h"
#include "SocketTypes.h"
#include "Sockets.h"

DEFINE_LOG_CATEGORY(LogVHReceiver);

namespace
{
constexpr int32 MaxTcpChunkSize  = 8192;
constexpr int32 MaxBufferedBytes = 1024 * 1024;

float Clamp01(const float Value)
{
    return FMath::Clamp(Value, 0.0f, 1.0f);
}

bool IsBenignRecvNoData(const int32 LastErrorCode)
{
    if (LastErrorCode == SE_EWOULDBLOCK)
    {
        return true;
    }
    if (LastErrorCode == 0)
    {
        return true;
    }
    return false;
}

FString JoinMorphNames(const TArray<FString>& Names)
{
    FString Out;
    for (int32 I = 0; I < Names.Num(); ++I)
    {
        if (I > 0)
        {
            Out += TEXT(",");
        }
        Out += Names[I];
    }
    return Out;
}
} // namespace

// ---------------------------------------------------------------------------
// LogLifecycle — 追查 ListenSocket / ClientSocket 与 10061（无监听）
// ---------------------------------------------------------------------------

void UExpressionReceiverComponent::LogLifecycle(const TCHAR* CallSite, const TCHAR* Detail) const
{
    if (!bVerboseLifecycleLog)
    {
        return;
    }
    const UWorld* W = GetWorld();
    const FString MeshName = GetNameSafe(ResolvedTargetMesh.Get());
    UE_LOG(LogVHReceiver, Log,
        TEXT("VHReceiver: [LIFE] %s | %s | Listen=%s Client=%s port=%d mesh=%s world=%s"),
        CallSite,
        Detail ? Detail : TEXT("-"),
        ListenSocket ? TEXT("OK") : TEXT("NULL"),
        ClientSocket ? TEXT("OK") : TEXT("NULL"),
        ListenPort,
        *MeshName,
        W ? *W->GetName() : TEXT("NULL"));
}

UExpressionReceiverComponent::UExpressionReceiverComponent()
{
    PrimaryComponentTick.bCanEverTick = true;
    PrimaryComponentTick.TickGroup = TG_PostUpdateWork;
}

FString UExpressionReceiverComponent::GetResolvedTargetMeshName() const
{
    return ResolvedTargetMesh ? FString(GetNameSafe(ResolvedTargetMesh.Get())) : FString();
}

USkeletalMeshComponent* UExpressionReceiverComponent::GetResolvedTargetMesh() const
{
    return ResolvedTargetMesh.Get();
}

void UExpressionReceiverComponent::LogSkeletalMeshCandidatesOnOwner(AActor* OwnerActor) const
{
    if (!OwnerActor)
    {
        UE_LOG(LogVHReceiver, Warning,
            TEXT("[VHReceiver][Init] Mesh candidates on owner: (no owner)"));
        return;
    }

    TArray<USkeletalMeshComponent*> Meshes;
    OwnerActor->GetComponents<USkeletalMeshComponent>(Meshes);

    UE_LOG(LogVHReceiver, Log,
        TEXT("[VHReceiver][Init] Mesh candidates on owner: count=%d actor=%s"),
        Meshes.Num(),
        *GetNameSafe(OwnerActor));

    for (USkeletalMeshComponent* M : Meshes)
    {
        if (!M)
        {
            continue;
        }
        const FString CompName = GetNameSafe(M);
        const FString Readable = FString::Printf(TEXT("%s.%s"), *GetNameSafe(OwnerActor), *CompName);
        const FString AssetNm  = GetNameSafe(M->GetSkeletalMeshAsset());
        UE_LOG(LogVHReceiver, Log,
            TEXT("[VHReceiver][Init]   - name=%s readable=%s asset=%s"),
            *CompName,
            *Readable,
            *AssetNm);
    }
}

FString UExpressionReceiverComponent::BuildCandidateNameList(const TArray<USkeletalMeshComponent*>& Meshes)
{
    FString R;
    for (USkeletalMeshComponent* M : Meshes)
    {
        if (!M)
        {
            continue;
        }
        if (!R.IsEmpty())
        {
            R += TEXT(", ");
        }
        R += GetNameSafe(M);
    }
    return R;
}

USkeletalMeshComponent* UExpressionReceiverComponent::FindOwnerSkeletalMeshByPreferredName(
    AActor* OwnerActor,
    const FString& PreferredIn)
{
    if (!OwnerActor)
    {
        return nullptr;
    }

    const FString Preferred = PreferredIn.TrimStartAndEnd();
    if (Preferred.IsEmpty())
    {
        return nullptr;
    }

    TArray<USkeletalMeshComponent*> Meshes;
    OwnerActor->GetComponents<USkeletalMeshComponent>(Meshes);

    auto MatchesPreferred = [&Preferred](const FString& ComponentName) -> bool
    {
        if (ComponentName.Equals(Preferred, ESearchCase::IgnoreCase))
        {
            return true;
        }
        FString Stripped = ComponentName;
        if (Stripped.EndsWith(TEXT("_GEN_VARIABLE"), ESearchCase::IgnoreCase))
        {
            Stripped.LeftChopInline(13, EAllowShrinking::No);
            return Stripped.Equals(Preferred, ESearchCase::IgnoreCase);
        }
        return false;
    };

    for (USkeletalMeshComponent* M : Meshes)
    {
        if (M && MatchesPreferred(FString(M->GetName())))
        {
            return M;
        }
    }

    return nullptr;
}

void UExpressionReceiverComponent::ValidateTargetMeshOwnershipAfterResolve()
{
    bMorphApplyBlockedByCrossActorTarget = false;
    if (!ResolvedTargetMesh)
    {
        return;
    }

    AActor* ReceiverOwner = GetOwner();
    AActor* MeshOwner     = ResolvedTargetMesh->GetOwner();
    const FString MeshCompName = GetNameSafe(ResolvedTargetMesh.Get());

    if (ReceiverOwner == MeshOwner)
    {
        return;
    }

    UE_LOG(LogVHReceiver, Error,
        TEXT("[VHReceiver][Init] Cross-Actor TargetMesh | receiver=%s receiver_owner=%s | target_mesh=%s target_owner=%s"),
        *GetName(),
        *GetNameSafe(ReceiverOwner),
        *MeshCompName,
        *GetNameSafe(MeshOwner));

    if (bRequireTargetMeshOwnedBySameActor)
    {
        bMorphApplyBlockedByCrossActorTarget = true;
        UE_LOG(LogVHReceiver, Error,
            TEXT("[VHReceiver][Init] Morph apply BLOCKED — bRequireTargetMeshOwnedBySameActor=true (TargetMesh must belong to same Actor as ExpressionReceiver)."));
    }
    else
    {
        UE_LOG(LogVHReceiver, Warning,
            TEXT("[VHReceiver][Init] Cross-Actor binding allowed — bRequireTargetMeshOwnedBySameActor=false; morph will still apply to foreign mesh."));
    }
}

bool UExpressionReceiverComponent::CanApplyMorphsThisFrame() const
{
    return ResolvedTargetMesh != nullptr && !bMorphApplyBlockedByCrossActorTarget;
}

void UExpressionReceiverComponent::DebugApplyMorph(const FString& MorphName, const float Value)
{
    const FString Trimmed = MorphName.TrimStartAndEnd();
    if (Trimmed.IsEmpty())
    {
        UE_LOG(LogVHReceiver, Error,
            TEXT("[VHReceiver][DebugApply] ERROR MorphName is empty | receiver=%s owner=%s"),
            *GetName(), *GetNameSafe(GetOwner()));
        return;
    }

    if (!ResolvedTargetMesh)
    {
        UE_LOG(LogVHReceiver, Error,
            TEXT("[VHReceiver][DebugApply] ERROR ResolvedTargetMesh is null | receiver=%s owner=%s — set Target Mesh Component picker or Preferred Target Mesh Name."),
            *GetName(), *GetNameSafe(GetOwner()));
        return;
    }

    if (bMorphApplyBlockedByCrossActorTarget)
    {
        UE_LOG(LogVHReceiver, Error,
            TEXT("[VHReceiver][DebugApply] ERROR morph apply blocked (cross-Actor target) | receiver=%s morph=%s"),
            *GetName(), *Trimmed);
        return;
    }

    const float Clamped = Clamp01(Value);
    UE_LOG(LogVHReceiver, Log,
        TEXT("[VHReceiver][DebugApply] target=%s morph=%s value=%.3f (invoking SetMorphTargetSafe)"),
        *GetNameSafe(ResolvedTargetMesh.Get()),
        *Trimmed,
        Clamped);

    SetMorphTargetSafe(FName(*Trimmed), Clamped, TEXT("DebugApplyMorph"));
}

void UExpressionReceiverComponent::ResolveAndLogTargetMesh()
{
    ResolvedTargetMesh                   = nullptr;
    bMorphApplyBlockedByCrossActorTarget = false;

    AActor* OwnerActor = GetOwner();
    const FString ReceiverName = GetName();
    const FString OwnerName    = GetNameSafe(OwnerActor);

    LogSkeletalMeshCandidatesOnOwner(OwnerActor);

    if (!OwnerActor)
    {
        UE_LOG(LogVHReceiver, Error,
            TEXT("[VHReceiver][Init][Error] Receiver=%s Owner=NULL. Cannot resolve skeletal mesh."),
            *ReceiverName);
        return;
    }

    TArray<USkeletalMeshComponent*> AllMeshes;
    OwnerActor->GetComponents<USkeletalMeshComponent>(AllMeshes);

    const bool bRefNameSpecified = !TargetMeshComponentRef.ComponentProperty.IsNone();

    if (bRefNameSpecified)
    {
        // UE 5.7+: FComponentReference::GetComponent(AActor* OwningActor) returns UActorComponent*
        UActorComponent* RefComp = TargetMeshComponentRef.GetComponent(OwnerActor);
        if (!RefComp)
        {
            UE_LOG(LogVHReceiver, Error,
                TEXT("[VHReceiver][Init] TargetMeshComponentRef '%s' could not be resolved on owner — check picker / component name. Candidates: [ %s ]"),
                *TargetMeshComponentRef.ComponentProperty.ToString(),
                *BuildCandidateNameList(AllMeshes));
            return;
        }

        USkeletalMeshComponent* RefSkel = Cast<USkeletalMeshComponent>(RefComp);
        if (!RefSkel)
        {
            UE_LOG(LogVHReceiver, Error,
                TEXT("[VHReceiver][Init] TargetMeshComponentRef points to '%s' which is not a USkeletalMeshComponent. Candidates: [ %s ]"),
                *GetNameSafe(RefComp),
                *BuildCandidateNameList(AllMeshes));
            return;
        }

        if (RefSkel->GetOwner() != OwnerActor)
        {
            UE_LOG(LogVHReceiver, Error,
                TEXT("[VHReceiver][Init] TargetMeshComponentRef resolved component owner mismatch (expected same as receiver owner)."));
        }

        ResolvedTargetMesh = RefSkel;
        USkeletalMesh* Asset = ResolvedTargetMesh->GetSkeletalMeshAsset();
        UE_LOG(LogVHReceiver, Log,
            TEXT("[VHReceiver][Init] TargetMesh bind=ManualComponentRef | receiver=%s owner=%s ref=%s target=%s asset=%s"),
            *ReceiverName,
            *OwnerName,
            *TargetMeshComponentRef.ComponentProperty.ToString(),
            *GetNameSafe(ResolvedTargetMesh.Get()),
            *GetNameSafe(Asset));
        if (!Asset)
        {
            UE_LOG(LogVHReceiver, Error,
                TEXT("[VHReceiver][Init] Resolved mesh has no SkeletalMeshAsset."));
        }
    }
    else
    {
        const FString PrefTrim = PreferredTargetMeshName.TrimStartAndEnd();
        if (PrefTrim.IsEmpty())
        {
            UE_LOG(LogVHReceiver, Error,
                TEXT("[VHReceiver][Init][Error] Receiver=%s Owner=%s — Target Mesh Component is unset and Preferred Target Mesh Name is empty. Set the component picker to miku_mesh or set Preferred Target Mesh Name. Candidates: [ %s ]"),
                *ReceiverName,
                *OwnerName,
                *BuildCandidateNameList(AllMeshes));
            return;
        }

        ResolvedTargetMesh = FindOwnerSkeletalMeshByPreferredName(OwnerActor, PrefTrim);
        if (!ResolvedTargetMesh)
        {
            UE_LOG(LogVHReceiver, Error,
                TEXT("[VHReceiver][Init][Error] No USkeletalMeshComponent named '%s' on owner. Candidates: [ %s ] — use Target Mesh Component picker or fix Preferred Target Mesh Name."),
                *PrefTrim,
                *BuildCandidateNameList(AllMeshes));
            return;
        }

        USkeletalMesh* Asset = ResolvedTargetMesh->GetSkeletalMeshAsset();
        UE_LOG(LogVHReceiver, Log,
            TEXT("[VHReceiver][Init] TargetMesh bind=ByPreferredName | receiver=%s owner=%s preferred=%s target=%s asset=%s"),
            *ReceiverName,
            *OwnerName,
            *PrefTrim,
            *GetNameSafe(ResolvedTargetMesh.Get()),
            *GetNameSafe(Asset));
        if (!Asset)
        {
            UE_LOG(LogVHReceiver, Error,
                TEXT("[VHReceiver][Init] Resolved mesh has no SkeletalMeshAsset."));
        }
    }

    if (ResolvedTargetMesh)
    {
        const FString PrefTrim = PreferredTargetMeshName.TrimStartAndEnd();
        if (!PrefTrim.IsEmpty())
        {
            const FString GotName = GetNameSafe(ResolvedTargetMesh.Get());
            if (!GotName.Equals(PrefTrim, ESearchCase::IgnoreCase))
            {
                FString Stripped = GotName;
                if (Stripped.EndsWith(TEXT("_GEN_VARIABLE"), ESearchCase::IgnoreCase))
                {
                    Stripped.LeftChopInline(13, EAllowShrinking::No);
                }
                if (!Stripped.Equals(PrefTrim, ESearchCase::IgnoreCase))
                {
                    UE_LOG(LogVHReceiver, Warning,
                        TEXT("[VHReceiver][Init] Resolved target name '%s' differs from Preferred Target Mesh Name '%s' — confirm this is intentional."),
                        *GotName,
                        *PrefTrim);
                }
            }
        }

        ValidateTargetMeshOwnershipAfterResolve();
        UE_LOG(LogVHReceiver, Log,
            TEXT("[VHReceiver][Init] Resolved TargetMesh name=%s (GetResolvedTargetMeshName)"),
            *GetResolvedTargetMeshName());
    }
}

void UExpressionReceiverComponent::BeginPlay()
{
    Super::BeginPlay();
    LogLifecycle(TEXT("BeginPlay"), TEXT("enter"));

    ResolveAndLogTargetMesh();

    if (ResolvedTargetMesh && CanApplyMorphsThisFrame())
    {
        AddTickPrerequisiteComponent(ResolvedTargetMesh);
        UE_LOG(LogVHReceiver, Log,
            TEXT("[VHReceiver][Init] Tick order: ExpressionReceiver runs after target=%s (PostUpdateWork)."),
            *GetNameSafe(ResolvedTargetMesh.Get()));
    }
    else if (!ResolvedTargetMesh)
    {
        UE_LOG(LogVHReceiver, Error,
            TEXT("[VHReceiver][Init] No ResolvedTargetMesh — listening may run, but Probe / SetMorphTarget are disabled until Target Mesh Component / Preferred Name is valid."));
    }
    else if (bMorphApplyBlockedByCrossActorTarget)
    {
        UE_LOG(LogVHReceiver, Error,
            TEXT("[VHReceiver][Init] Target present but morph apply is BLOCKED (cross-Actor). Fix ownership or disable bRequireTargetMeshOwnedBySameActor."));
    }

    StartListening();

    if (!ResolvedTargetMesh || !ResolvedTargetMesh->GetSkeletalMeshAsset())
    {
        UE_LOG(LogVHReceiver, Warning,
            TEXT("[VHReceiver][Probe] Skipped — ResolvedTargetMesh null or no asset."));
        LogLifecycle(TEXT("BeginPlay"), TEXT("exit (no mesh for probe)"));
        return;
    }

    if (!bRunMorphProbeOnBeginPlay)
    {
        UE_LOG(LogVHReceiver, Log,
            TEXT("[VHReceiver][Probe] Skipped — bRunMorphProbeOnBeginPlay=false"));
        LogLifecycle(TEXT("BeginPlay"), TEXT("exit"));
        return;
    }

    UE_LOG(LogVHReceiver, Log,
        TEXT("[VHReceiver][Probe] ======== morph existence probe (asset-only, NOT apply) target=%s ========"),
        *GetNameSafe(ResolvedTargetMesh.Get()));
    int32 OkCount  = 0;
    int32 MisCount = 0;

    for (const TPair<FString, FString>& Pair : EffectiveMorphMap)
    {
        if (Pair.Value.IsEmpty())
        {
            UE_LOG(LogVHReceiver, Log,
                TEXT("[VHReceiver][Probe]   [blendshape] %-20s  ->  (empty, skipped)"), *Pair.Key);
            continue;
        }
        const FName MorphName(*Pair.Value.TrimStartAndEnd());
        const bool bExists =
            ResolvedTargetMesh->GetSkeletalMeshAsset()->FindMorphTarget(MorphName) != nullptr;
        UE_LOG(LogVHReceiver, Log,
            TEXT("[VHReceiver][Probe]   [blendshape] %-20s  ->  '%s'  [%s]"),
            *Pair.Key, *Pair.Value, bExists ? TEXT("OK") : TEXT("MISSING"));
        bExists ? ++OkCount : ++MisCount;
    }

    for (const TPair<FString, FString>& Pair : EffectivePhonemeMap)
    {
        if (Pair.Value.IsEmpty())
        {
            continue;
        }
        const FName MorphName(*Pair.Value.TrimStartAndEnd());
        const bool bExists =
            ResolvedTargetMesh->GetSkeletalMeshAsset()->FindMorphTarget(MorphName) != nullptr;
        UE_LOG(LogVHReceiver, Log,
            TEXT("[VHReceiver][Probe]   [phoneme]    %-20s  ->  '%s'  [%s]"),
            *Pair.Key, *Pair.Value, bExists ? TEXT("OK") : TEXT("MISSING"));
        bExists ? ++OkCount : ++MisCount;
    }

    UE_LOG(LogVHReceiver, Log,
        TEXT("[VHReceiver][Probe] ======== probe done: %d OK, %d MISSING (see [VHReceiver][Apply] for real SetMorphTarget) ========"),
        OkCount, MisCount);

    if (MisCount > 0)
    {
        UE_LOG(LogVHReceiver, Warning,
            TEXT("[VHReceiver][Probe] %d morph name(s) MISSING on asset — use MorphNameOverrides."), MisCount);
    }
    LogLifecycle(TEXT("BeginPlay"), TEXT("exit"));
}

void UExpressionReceiverComponent::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
    UE_LOG(LogVHReceiver, Log,
        TEXT("VHReceiver: EndPlay — EEndPlayReason=%d (PIE stop / actor destroy / level unload). "
             "CloseAll() will destroy ListenSocket — until next StartListening, port is closed (WSA 10061)."),
        static_cast<int32>(EndPlayReason));
    LogLifecycle(TEXT("EndPlay"), TEXT("see EEndPlayReason int above"));
    CloseAll(TEXT("EndPlay -> CloseAll"));
    Super::EndPlay(EndPlayReason);
}

void UExpressionReceiverComponent::TickComponent(
    const float DeltaTime,
    const ELevelTick TickType,
    FActorComponentTickFunction* ThisTickFunction)
{
    Super::TickComponent(DeltaTime, TickType, ThisTickFunction);

    if (!ListenSocket)
    {
        if (bVerboseLifecycleLog && GetWorld())
        {
            const double T = GetWorld()->GetTimeSeconds();
            if (LastListenSocketNullLogTime < 0.0 || (T - LastListenSocketNullLogTime) > 2.0)
            {
                LastListenSocketNullLogTime = T;
                UE_LOG(LogVHReceiver, Warning,
                    TEXT("VHReceiver: [LIFE] TickComponent — ListenSocket=NULL (nothing listening on port %d). "
                         "WSA 10061 if sender connects. Cause: CloseAll/StopListening/StartListening failed earlier."),
                    ListenPort);
            }
        }
    }

    if (ClientSocket)
    {
        ReadAndProcessData();

        if (ClientSocket && ClientSocket->GetConnectionState() != SCS_Connected)
        {
            UE_LOG(LogVHReceiver, Log,
                TEXT("VHReceiver: post-read socket state != Connected — closing client."));
            CloseClient(TEXT("TickComponent: GetConnectionState!=SCS_Connected after Read"));
        }
    }

    TryAcceptConnection();

    if (FaceResetTimeoutSecs > 0.0f && LastFrameReceivedTime >= 0.0 &&
        PrevDrivenMorphs.Num() > 0)
    {
        if (const UWorld* World = GetWorld())
        {
            const double IdleSecs = World->GetTimeSeconds() - LastFrameReceivedTime;
            if (IdleSecs > static_cast<double>(FaceResetTimeoutSecs))
            {
                UE_LOG(LogVHReceiver, Log,
                    TEXT("VHReceiver: ResetFaceMorphs — idle %.2f s > FaceResetTimeoutSecs=%.1f"),
                    IdleSecs, FaceResetTimeoutSecs);
                ResetFaceMorphs();
            }
        }
    }
}

TMap<FString, FString> UExpressionReceiverComponent::BuildDefaultMorphMap(
    const bool bInSwapWinkLeftRight)
{
    TMap<FString, FString> DefaultMap;
    DefaultMap.Add(TEXT("jawOpen"),          TEXT("あ"));
    DefaultMap.Add(TEXT("mouthSmile"),       TEXT("口角上げ"));
    DefaultMap.Add(TEXT("mouthFrown"),       TEXT("困る"));
    DefaultMap.Add(TEXT("eyeBlinkLeft"),     bInSwapWinkLeftRight ? TEXT("ウィンク右") : TEXT("ウィンク"));
    DefaultMap.Add(TEXT("eyeBlinkRight"),    bInSwapWinkLeftRight ? TEXT("ウィンク")   : TEXT("ウィンク右"));
    DefaultMap.Add(TEXT("eyeSquintLeft"),    TEXT("笑い"));
    DefaultMap.Add(TEXT("eyeSquintRight"),   TEXT("笑い"));
    DefaultMap.Add(TEXT("eyeWideLeft"),      TEXT("びっくり"));
    DefaultMap.Add(TEXT("eyeWideRight"),     TEXT("びっくり"));
    DefaultMap.Add(TEXT("browInnerUp"),      TEXT("はぅ"));
    DefaultMap.Add(TEXT("browDown"),         TEXT("怒り"));
    DefaultMap.Add(TEXT("browOuterUpLeft"),  TEXT("上_左"));
    DefaultMap.Add(TEXT("browOuterUpRight"), TEXT("上_右"));
    DefaultMap.Add(TEXT("noseSneerLeft"),    TEXT(""));
    DefaultMap.Add(TEXT("noseSneerRight"),   TEXT(""));
    return DefaultMap;
}

TMap<FString, FString> UExpressionReceiverComponent::BuildDefaultPhonemeMap()
{
    TMap<FString, FString> DefaultMap;
    DefaultMap.Add(TEXT("a"), TEXT("あ"));
    DefaultMap.Add(TEXT("i"), TEXT("い"));
    DefaultMap.Add(TEXT("u"), TEXT("う"));
    DefaultMap.Add(TEXT("e"), TEXT("え"));
    DefaultMap.Add(TEXT("o"), TEXT("お"));
    return DefaultMap;
}

void UExpressionReceiverComponent::RefreshMapping()
{
    EffectiveMorphMap = BuildDefaultMorphMap(bSwapWinkLeftRight);
    for (const TPair<FString, FString>& Pair : MorphNameOverrides)
    {
        EffectiveMorphMap.Add(Pair.Key, Pair.Value);
    }

    EffectivePhonemeMap = BuildDefaultPhonemeMap();
    for (const TPair<FString, FString>& Pair : PhonemeNameOverrides)
    {
        EffectivePhonemeMap.Add(Pair.Key, Pair.Value);
    }

    VerifiedMorphNames.Empty();
    WarnedMissingMorphs.Empty();

    UE_LOG(LogVHReceiver, Log,
        TEXT("[VHReceiver][Init] mapping ready — %d blendshape entries, %d phoneme entries."),
        EffectiveMorphMap.Num(), EffectivePhonemeMap.Num());

    for (const TPair<FString, FString>& Pair : EffectiveMorphMap)
    {
        UE_LOG(LogVHReceiver, Log, TEXT("[VHReceiver][Init]   map %s  ->  '%s'"), *Pair.Key, *Pair.Value);
    }
}

void UExpressionReceiverComponent::ResetFaceMorphs()
{
    UE_LOG(LogVHReceiver, Log,
        TEXT("VHReceiver: ResetFaceMorphs — clearing %d driven morph(s). bClearMorphsEachFrame=%d"),
        PrevDrivenMorphs.Num(), bClearMorphsEachFrame ? 1 : 0);

    if (!ResolvedTargetMesh || !CanApplyMorphsThisFrame())
    {
        PrevDrivenMorphs.Empty();
        return;
    }

    TArray<FString> ClearedNames;
    for (const FName MorphName : PrevDrivenMorphs)
    {
        ClearedNames.Add(MorphName.ToString());
        SetMorphTargetSafe(MorphName, 0.0f, TEXT("ResetFaceMorphs"));
    }

    if (bLogMorphClearOperations && ClearedNames.Num() > 0)
    {
        UE_LOG(LogVHReceiver, Log,
            TEXT("[VHReceiver][Clear] target=%s cleared=%d names=%s ctx=ResetFaceMorphs"),
            *GetNameSafe(ResolvedTargetMesh.Get()),
            ClearedNames.Num(),
            *JoinMorphNames(ClearedNames));
    }

    PrevDrivenMorphs.Empty();
    LastFrameReceivedTime = -1.0;
}

void UExpressionReceiverComponent::StartListening()
{
    LogLifecycle(TEXT("StartListening"), TEXT("before CloseAll(rebind)"));
    CloseAll(TEXT("StartListening: rebind — close previous Listen+Client"));

    RefreshMapping();

    const int32 SafePort = FMath::Clamp(ListenPort, 1, 65535);
    if (SafePort != ListenPort)
    {
        UE_LOG(LogVHReceiver, Warning,
            TEXT("VHReceiver: invalid ListenPort %d, clamped to %d."), ListenPort, SafePort);
        ListenPort = SafePort;
    }

    ISocketSubsystem* SocketSubsystem = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM);
    if (!SocketSubsystem)
    {
        UE_LOG(LogVHReceiver, Error, TEXT("VHReceiver: ISocketSubsystem unavailable."));
        return;
    }

    ListenSocket = SocketSubsystem->CreateSocket(NAME_Stream, TEXT("VHReceiverListen"), false);
    if (!ListenSocket)
    {
        UE_LOG(LogVHReceiver, Error, TEXT("VHReceiver: failed to create listen socket."));
        return;
    }

    ListenSocket->SetNonBlocking(true);
    ListenSocket->SetReuseAddr(true);

    TSharedRef<FInternetAddr> Address = SocketSubsystem->CreateInternetAddr();
    Address->SetAnyAddress();
    Address->SetPort(ListenPort);

    if (!ListenSocket->Bind(*Address))
    {
        UE_LOG(LogVHReceiver, Error,
            TEXT("VHReceiver: bind failed on port %d — ListenSocket destroyed."), ListenPort);
        CloseAll(TEXT("StartListening: BindFailed"));
        return;
    }

    if (!ListenSocket->Listen(8))
    {
        UE_LOG(LogVHReceiver, Error,
            TEXT("VHReceiver: listen() failed on port %d — ListenSocket destroyed."), ListenPort);
        CloseAll(TEXT("StartListening: ListenFailed"));
        return;
    }

    UE_LOG(LogVHReceiver, Log,
        TEXT("[VHReceiver][Init] StartListening OK — port %d backlog=8. Only EndPlay/StopListening/CloseAll closes listener."),
        ListenPort);
    LogLifecycle(TEXT("StartListening"), TEXT("ListenSocket bound"));
}

void UExpressionReceiverComponent::StopListening()
{
    UE_LOG(LogVHReceiver, Log, TEXT("VHReceiver: StopListening() called explicitly."));
    LogLifecycle(TEXT("StopListening"), TEXT("user/API"));
    CloseAll(TEXT("StopListening"));
}

bool UExpressionReceiverComponent::IsClientConnected() const
{
    return ClientSocket != nullptr &&
           ClientSocket->GetConnectionState() == SCS_Connected;
}

void UExpressionReceiverComponent::TryAcceptConnection()
{
    if (!ListenSocket)
    {
        return;
    }

    bool bHasPendingConnection = false;
    if (!ListenSocket->HasPendingConnection(bHasPendingConnection) || !bHasPendingConnection)
    {
        return;
    }

    LogLifecycle(TEXT("TryAcceptConnection"), TEXT("HasPendingConnection=true"));

    if (ClientSocket)
    {
        const bool bStillConnected =
            ClientSocket->GetConnectionState() == SCS_Connected;

        if (!bStillConnected)
        {
            UE_LOG(LogVHReceiver, Log,
                TEXT("VHReceiver: TryAccept — old client not connected, CloseClient before Accept."));
            CloseClient(TEXT("TryAccept: stale NotConnected"));
        }
        else
        {
            const UWorld* World = GetWorld();
            const double IdleSecs = (World && LastSocketActivityTime >= 0.0)
                ? (World->GetTimeSeconds() - LastSocketActivityTime)
                : (PendingAcceptIdleForceCloseSecs + 1.0);

            const double ForceLimit = FMath::Max(
                static_cast<double>(PendingAcceptIdleForceCloseSecs), 0.05);

            if (IdleSecs >= ForceLimit)
            {
                UE_LOG(LogVHReceiver, Log,
                    TEXT("VHReceiver: TryAccept — force CloseClient idle=%.3fs >= %.3fs (new sender waiting)."),
                    IdleSecs, ForceLimit);
                CloseClient(TEXT("TryAccept: PendingAcceptIdleForceClose"));
            }
            else
            {
                UE_LOG(LogVHReceiver, Verbose,
                    TEXT("VHReceiver: TryAccept — defer (idle=%.3f < %.3f), backlog holds new SYN."),
                    IdleSecs, ForceLimit);
                return;
            }
        }
    }

    ClientSocket = ListenSocket->Accept(TEXT("VHReceiverClient"));
    if (ClientSocket)
    {
        ClientSocket->SetNonBlocking(true);
        ReceiveBuffer.Reset();
        bLoggedFirstFrameSample = false;

        const double Now = GetWorld() ? GetWorld()->GetTimeSeconds() : 0.0;
        LastSocketActivityTime = Now;

        UE_LOG(LogVHReceiver, Log,
            TEXT("VHReceiver: Accept OK — new ClientSocket. ListenSocket unchanged. port=%d frames=%lld"),
            ListenPort, FramesAppliedTotal);
        LogLifecycle(TEXT("TryAcceptConnection"), TEXT("accepted"));
    }
    else
    {
        UE_LOG(LogVHReceiver, Warning,
            TEXT("VHReceiver: HasPendingConnection but Accept returned null."));
    }
}

void UExpressionReceiverComponent::CloseClient(const TCHAR* Reason)
{
    if (!ClientSocket)
    {
        return;
    }

    UE_LOG(LogVHReceiver, Log,
        TEXT("VHReceiver: CloseClient — reason=%s | ListenSocket=%s (listener stays open)"),
        Reason,
        ListenSocket ? TEXT("OK") : TEXT("NULL"));

    if (ISocketSubsystem* SocketSubsystem = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM))
    {
        ClientSocket->Close();
        SocketSubsystem->DestroySocket(ClientSocket);
    }

    ClientSocket = nullptr;
    ReceiveBuffer.Reset();
    LastSocketActivityTime = -1.0;

    UE_LOG(LogVHReceiver, Log,
        TEXT("VHReceiver: CloseClient done — face NOT zeroed here; FaceResetTimeout=%.1f s handles reset."),
        FaceResetTimeoutSecs);
}

void UExpressionReceiverComponent::CloseAll(const TCHAR* Reason)
{
    UE_LOG(LogVHReceiver, Log,
        TEXT("VHReceiver: CloseAll — reason=%s | will destroy ListenSocket+ClientSocket"),
        Reason);
    LogLifecycle(TEXT("CloseAll"), Reason);

    CloseClient(TEXT("CloseAll: closing ClientSocket first (ListenSocket closed next)"));

    if (ListenSocket)
    {
        if (ISocketSubsystem* SocketSubsystem = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM))
        {
            ListenSocket->Close();
            SocketSubsystem->DestroySocket(ListenSocket);
        }

        ListenSocket = nullptr;
    }

    UE_LOG(LogVHReceiver, Warning,
        TEXT("VHReceiver: CloseAll done — port %d is NO LONGER listening until StartListening(). "
             "WSA10061 if sender connects now."),
        ListenPort);
}

void UExpressionReceiverComponent::ReadAndProcessData()
{
    if (!ClientSocket)
    {
        return;
    }

    TArray<uint8> TempBuffer;
    TempBuffer.SetNumUninitialized(MaxTcpChunkSize);

    bool bReceivedAnyData = false;
    uint32 PendingSize = 0;
    int32  TotalBytesThisTick = 0;

    while (ClientSocket->HasPendingData(PendingSize) && PendingSize > 0)
    {
        const int32 BytesToRead = FMath::Min(static_cast<int32>(PendingSize), MaxTcpChunkSize);
        int32 BytesRead = 0;

        const bool bRecvOk = ClientSocket->Recv(TempBuffer.GetData(), BytesToRead, BytesRead);
        if (!bRecvOk)
        {
            ISocketSubsystem* Subsys = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM);
            const int32 LastErr = Subsys ? Subsys->GetLastErrorCode() : -1;
            if (IsBenignRecvNoData(LastErr))
            {
                break;
            }
            UE_LOG(LogVHReceiver, Warning,
                TEXT("VHReceiver: ReadAndProcessData Recv fatal LastError=%d — CloseClient"), LastErr);
            CloseClient(TEXT("ReadAndProcessData: Recv fatal"));
            return;
        }

        if (BytesRead <= 0)
        {
            if (ClientSocket->GetConnectionState() != SCS_Connected)
            {
                CloseClient(TEXT("ReadAndProcessData: recv=0 and !Connected"));
                return;
            }
            break;
        }

        ReceiveBuffer.Append(TempBuffer.GetData(), BytesRead);
        bReceivedAnyData = true;
        TotalBytesThisTick += BytesRead;
    }

    if (bReceivedAnyData && GetWorld())
    {
        LastSocketActivityTime = GetWorld()->GetTimeSeconds();
    }

    if (bVerboseLifecycleLog && TotalBytesThisTick > 0)
    {
        UE_LOG(LogVHReceiver, Verbose,
            TEXT("VHReceiver: ReadAndProcessData — tcp bytes=%d buffer=%d"),
            TotalBytesThisTick, ReceiveBuffer.Num());
    }

    int32 NewLineIndex = INDEX_NONE;
    while (ReceiveBuffer.Find(static_cast<uint8>('\n'), NewLineIndex))
    {
        TArray<uint8> LineBytes;
        LineBytes.Append(ReceiveBuffer.GetData(), NewLineIndex);
        ReceiveBuffer.RemoveAt(0, NewLineIndex + 1, EAllowShrinking::No);

        while (LineBytes.Num() > 0 &&
               (LineBytes.Last() == static_cast<uint8>('\r') ||
                LineBytes.Last() == static_cast<uint8>(' ')))
        {
            LineBytes.Pop();
        }

        if (LineBytes.Num() == 0)
        {
            continue;
        }

        LineBytes.Add(0);
        const FString JsonLine =
            FString(UTF8_TO_TCHAR(reinterpret_cast<const char*>(LineBytes.GetData())))
            .TrimStartAndEnd();

        if (!JsonLine.IsEmpty())
        {
            ApplyFrame(JsonLine);
        }
    }

    if (ReceiveBuffer.Num() > MaxBufferedBytes)
    {
        UE_LOG(LogVHReceiver, Warning,
            TEXT("VHReceiver: receive buffer overflow (%d bytes)"), ReceiveBuffer.Num());
        CloseClient(TEXT("ReadAndProcessData: buffer overflow"));
    }
}

bool UExpressionReceiverComponent::ApplyFrame(const FString& JsonLine)
{
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(JsonLine);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        UE_LOG(LogVHReceiver, Warning,
            TEXT("[VHReceiver][Parse] JSON parse FAILED — frame skipped. Raw: %.160s"), *JsonLine);
        return false;
    }

    if (!CanApplyMorphsThisFrame())
    {
        if (!bWarnedMissingTargetMesh)
        {
            if (!ResolvedTargetMesh)
            {
                UE_LOG(LogVHReceiver, Error,
                    TEXT("[VHReceiver][Parse] ResolvedTargetMesh null — frame skipped. Set Target Mesh Component or Preferred Target Mesh Name."));
            }
            else if (bMorphApplyBlockedByCrossActorTarget)
            {
                UE_LOG(LogVHReceiver, Error,
                    TEXT("[VHReceiver][Parse] Morph apply blocked (cross-Actor target) — frame skipped."));
            }
            bWarnedMissingTargetMesh = true;
        }
        return false;
    }
    bWarnedMissingTargetMesh = false;

    double SequenceIdNumber = 0.0;
    Root->TryGetNumberField(TEXT("sequence_id"), SequenceIdNumber);

    FString EmotionLabel = TEXT("neutral");
    FString PhonemeHint;
    float   AudioRms = 0.0f;

    const TSharedPtr<FJsonObject>* EmotionObject = nullptr;
    if (Root->TryGetObjectField(TEXT("emotion"), EmotionObject) &&
        EmotionObject && EmotionObject->IsValid())
    {
        (*EmotionObject)->TryGetStringField(TEXT("label"), EmotionLabel);
    }

    const TSharedPtr<FJsonObject>* AudioObject = nullptr;
    if (Root->TryGetObjectField(TEXT("audio"), AudioObject) &&
        AudioObject && AudioObject->IsValid())
    {
        double AudioRmsNumber = 0.0;
        (*AudioObject)->TryGetNumberField(TEXT("rms"), AudioRmsNumber);
        AudioRms = Clamp01(static_cast<float>(AudioRmsNumber));
        (*AudioObject)->TryGetStringField(TEXT("phoneme_hint"), PhonemeHint);
    }

    const TSharedPtr<FJsonObject>* BlendshapeObject = nullptr;
    int32 BlendshapeCount = 0;
    if (Root->TryGetObjectField(TEXT("blendshapes"), BlendshapeObject) &&
        BlendshapeObject && BlendshapeObject->IsValid())
    {
        BlendshapeCount = (*BlendshapeObject)->Values.Num();
    }

    const int64 SeqId = static_cast<int64>(SequenceIdNumber);
    const bool bStrideHit =
        (LogFrameStride <= 1) || (SeqId % static_cast<int64>(FMath::Max(LogFrameStride, 1)) == 0);
    const bool bShouldLogFrame =
        bLogFramesToOutput && (bStrideHit || !bLoggedFirstFrameSample);

    TMap<FName, float> PendingMorphWeights;

    if (bDebugMinimalMorphCycle)
    {
        const int32 Phase = static_cast<int32>(FMath::Abs(SeqId) % 3);
        if (Phase == 0)
        {
            PendingMorphWeights.Add(FName(TEXT("笑い")), 1.0f);
        }
        else if (Phase == 1)
        {
            PendingMorphWeights.Add(FName(TEXT("ウィンク")), 1.0f);
        }
        else
        {
            PendingMorphWeights.Add(FName(TEXT("びっくり")), 1.0f);
        }
        UE_LOG(LogVHReceiver, Log,
            TEXT("[VHReceiver][Parse] DEBUG bDebugMinimalMorphCycle — seq=%lld phase=%d (ignoring JSON blendshapes)"),
            SeqId, Phase);
        if (bLogParseMorphMapping && bShouldLogFrame)
        {
            const TCHAR* M = Phase == 0 ? TEXT("笑い") : (Phase == 1 ? TEXT("ウィンク") : TEXT("びっくり"));
            UE_LOG(LogVHReceiver, Log,
                TEXT("[VHReceiver][Parse] protocol=(debug_cycle) mapped=%s value=1.000 source=debug"),
                M);
        }
    }
    else if (BlendshapeObject && BlendshapeObject->IsValid())
    {
        for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair :
             (*BlendshapeObject)->Values)
        {
            if (!Pair.Value.IsValid() || Pair.Value->Type != EJson::Number)
            {
                if (bShouldLogFrame && Pair.Value.IsValid())
                {
                    UE_LOG(LogVHReceiver, Log,
                        TEXT("[VHReceiver][Parse] skip blendshape key '%s' — not a number (type=%d)"),
                        *Pair.Key, static_cast<int32>(Pair.Value->Type));
                }
                continue;
            }

            const FString MappedRaw       = ResolveMorphName(Pair.Key);
            const FString MorphNameString = MappedRaw.TrimStartAndEnd();
            const bool bFromOverride      = MorphNameOverrides.Contains(Pair.Key);

            if (MorphNameString.IsEmpty())
            {
                if (bLogParseMorphMapping && bShouldLogFrame)
                {
                    UE_LOG(LogVHReceiver, Warning,
                        TEXT("[VHReceiver][Parse][Warn] protocol=%s resolved morph is empty (check map / MorphNameOverrides)"),
                        *Pair.Key);
                }
                continue;
            }

            const FName MorphName(*MorphNameString);
            const float Weight = Clamp01(static_cast<float>(Pair.Value->AsNumber()));

            if (bLogParseMorphMapping && bShouldLogFrame)
            {
                UE_LOG(LogVHReceiver, Log,
                    TEXT("[VHReceiver][Parse] protocol=%s mapped=%s value=%.3f source=%s"),
                    *Pair.Key,
                    *MorphNameString,
                    Weight,
                    bFromOverride ? TEXT("override") : TEXT("default"));
            }

            float& Slot = PendingMorphWeights.FindOrAdd(MorphName);
            Slot = FMath::Max(Slot, Weight);
        }
    }

    const FString TrimmedPhoneme = PhonemeHint.TrimStartAndEnd();
    if (!bDebugMinimalMorphCycle &&
        !TrimmedPhoneme.IsEmpty() &&
        !TrimmedPhoneme.Equals(TEXT("rest"), ESearchCase::IgnoreCase))
    {
        const FString PhonemeMorphRaw = ResolvePhonemeMorphName(TrimmedPhoneme);
        const FString PhonemeMorphName = PhonemeMorphRaw.TrimStartAndEnd();
        const bool bPhonemeOverride    = PhonemeNameOverrides.Contains(TrimmedPhoneme);

        if (PhonemeMorphName.IsEmpty())
        {
            if (bLogParseMorphMapping && bShouldLogFrame)
            {
                UE_LOG(LogVHReceiver, Warning,
                    TEXT("[VHReceiver][Parse][Warn] phoneme protocol=%s resolved morph is empty (check PhonemeNameOverrides / default map)"),
                    *TrimmedPhoneme);
            }
        }
        else
        {
            const FName MorphName(*PhonemeMorphName);
            const float Weight = Clamp01(AudioRms * FMath::Max(PhonemeWeightScale, 0.0f));

            if (bLogParseMorphMapping && bShouldLogFrame)
            {
                UE_LOG(LogVHReceiver, Log,
                    TEXT("[VHReceiver][Parse] protocol=phoneme:%s mapped=%s value=%.3f source=%s rms=%.3f"),
                    *TrimmedPhoneme,
                    *PhonemeMorphName,
                    Weight,
                    bPhonemeOverride ? TEXT("override") : TEXT("default"),
                    AudioRms);
            }

            float& Slot = PendingMorphWeights.FindOrAdd(MorphName);
            Slot = FMath::Max(Slot, Weight);
        }
    }

    FString MorphListStr;
    for (const TPair<FName, float>& MP : PendingMorphWeights)
    {
        MorphListStr += FString::Printf(TEXT("%s=%.3f "), *MP.Key.ToString(), MP.Value);
    }

    if (bShouldLogFrame)
    {
        bLoggedFirstFrameSample = true;
        FString JsonPreview;
        if (LogJsonPreviewChars > 0)
        {
            JsonPreview = JsonLine.Left(LogJsonPreviewChars);
            if (JsonLine.Len() > LogJsonPreviewChars)
            {
                JsonPreview += TEXT("...");
            }
        }
        UE_LOG(LogVHReceiver, Log,
            TEXT("[VHReceiver][Parse] seq=%lld emotion='%s' phoneme='%s' rms=%.3f json_blendshapes=%d pending_morphs=%d | %s (next: [Apply] per morph on GameThread)"),
            SeqId,
            *EmotionLabel,
            *PhonemeHint,
            AudioRms,
            BlendshapeCount,
            PendingMorphWeights.Num(),
            PendingMorphWeights.Num() > 0 ? *MorphListStr : TEXT("(none)"));
        if (LogJsonPreviewChars > 0 && !JsonPreview.IsEmpty())
        {
            UE_LOG(LogVHReceiver, Log, TEXT("[VHReceiver][Parse]   json: %s"), *JsonPreview);
        }
    }

    if (bClearMorphsEachFrame)
    {
        TArray<FString> ClearedNames;
        for (const FName PreviousMorphName : PrevDrivenMorphs)
        {
            if (!PendingMorphWeights.Contains(PreviousMorphName))
            {
                ClearedNames.Add(PreviousMorphName.ToString());
                SetMorphTargetSafe(PreviousMorphName, 0.0f, TEXT("ClearMorphsEachFrame: not in pending"));
            }
        }
        if (bLogMorphClearOperations && ClearedNames.Num() > 0)
        {
            UE_LOG(LogVHReceiver, Log,
                TEXT("[VHReceiver][Clear] target=%s cleared=%d names=%s ctx=ClearMorphsEachFrame"),
                *GetNameSafe(ResolvedTargetMesh.Get()),
                ClearedNames.Num(),
                *JoinMorphNames(ClearedNames));
        }
    }

    TSet<FName> CurrentDrivenMorphs;
    for (const TPair<FName, float>& Pair : PendingMorphWeights)
    {
        SetMorphTargetSafe(Pair.Key, Pair.Value, TEXT("ApplyFrame"));
        CurrentDrivenMorphs.Add(Pair.Key);
    }

    PrevDrivenMorphs = MoveTemp(CurrentDrivenMorphs);
    LastSequenceId   = SeqId;
    LastEmotionLabel = EmotionLabel;
    ++FramesAppliedTotal;

    if (const UWorld* World = GetWorld())
    {
        LastFrameReceivedTime  = World->GetTimeSeconds();
        LastSocketActivityTime = LastFrameReceivedTime;
    }

    OnFrameReceived.Broadcast(LastSequenceId, LastEmotionLabel);
    return true;
}

FString UExpressionReceiverComponent::ResolveMorphName(const FString& ProtocolKey) const
{
    if (const FString* Found = EffectiveMorphMap.Find(ProtocolKey))
    {
        return *Found;
    }
    return ProtocolKey;
}

FString UExpressionReceiverComponent::ResolvePhonemeMorphName(const FString& PhonemeKey) const
{
    if (const FString* Found = EffectivePhonemeMap.Find(PhonemeKey))
    {
        return *Found;
    }
    return FString();
}

bool UExpressionReceiverComponent::HasMorphTarget(const FName MorphName)
{
    if (VerifiedMorphNames.Contains(MorphName))
    {
        return true;
    }

    if (WarnedMissingMorphs.Contains(MorphName))
    {
        return false;
    }

    if (!ResolvedTargetMesh)
    {
        return false;
    }

    const USkeletalMesh* SkeletalMesh = ResolvedTargetMesh->GetSkeletalMeshAsset();
    if (!SkeletalMesh || SkeletalMesh->FindMorphTarget(MorphName) == nullptr)
    {
        WarnedMissingMorphs.Add(MorphName);
        UE_LOG(LogVHReceiver, Warning,
            TEXT("[VHReceiver][Apply] WARN morph not found on asset | target=%s morph=%s asset=%s"),
            *GetNameSafe(ResolvedTargetMesh.Get()), *MorphName.ToString(), *GetNameSafe(SkeletalMesh));
        return false;
    }

    VerifiedMorphNames.Add(MorphName);
    return true;
}

void UExpressionReceiverComponent::SetMorphTargetSafe(
    const FName MorphName,
    const float Weight,
    const TCHAR* Context)
{
    if (!ResolvedTargetMesh || MorphName.IsNone() || bMorphApplyBlockedByCrossActorTarget)
    {
        return;
    }

    TWeakObjectPtr<UExpressionReceiverComponent> WeakThis(this);
    TObjectPtr<USkeletalMeshComponent> MeshAtSchedule = ResolvedTargetMesh;
    const float ClampedWeight = Clamp01(Weight);
    const FName MorphNameCopy = MorphName;

    auto ApplyOnGameThread = [WeakThis, MeshAtSchedule, MorphNameCopy, ClampedWeight, Context]()
    {
        if (!WeakThis.IsValid())
        {
            return;
        }

        UExpressionReceiverComponent* Self = WeakThis.Get();
        if (Self->bMorphApplyBlockedByCrossActorTarget)
        {
            return;
        }

        if (!IsValid(MeshAtSchedule) || MeshAtSchedule.Get() != Self->ResolvedTargetMesh.Get())
        {
            UE_LOG(LogVHReceiver, Error,
                TEXT("[VHReceiver][Apply] ERROR target invalid or reassigned | morph=%s expected_target=%s current_target=%s"),
                *MorphNameCopy.ToString(),
                *GetNameSafe(MeshAtSchedule.Get()),
                *GetNameSafe(Self->ResolvedTargetMesh.Get()));
            return;
        }

        if (!Self->ResolvedTargetMesh)
        {
            UE_LOG(LogVHReceiver, Error,
                TEXT("[VHReceiver][Apply] ERROR ResolvedTargetMesh null | morph=%s"),
                *MorphNameCopy.ToString());
            return;
        }

        USkeletalMesh* Asset = Self->ResolvedTargetMesh->GetSkeletalMeshAsset();
        if (!Asset)
        {
            UE_LOG(LogVHReceiver, Error,
                TEXT("[VHReceiver][Apply] ERROR no SkeletalMeshAsset | target=%s morph=%s"),
                *GetNameSafe(Self->ResolvedTargetMesh.Get()),
                *MorphNameCopy.ToString());
            return;
        }

        if (!Self->HasMorphTarget(MorphNameCopy))
        {
            return;
        }

        Self->ResolvedTargetMesh->SetMorphTarget(MorphNameCopy, ClampedWeight);
        Self->ResolvedTargetMesh->MarkRenderStateDirty();

        if (Self->bLogEachMorphApply)
        {
            const TCHAR* ThreadLabel = IsInGameThread() ? TEXT("GameThread") : TEXT("NonGameThread");
            UE_LOG(LogVHReceiver, Log,
                TEXT("[VHReceiver][Apply] target=%s morph=%s value=%.3f thread=%s ctx=%s asset=%s"),
                *GetNameSafe(Self->ResolvedTargetMesh.Get()),
                *MorphNameCopy.ToString(),
                ClampedWeight,
                ThreadLabel,
                Context ? Context : TEXT("-"),
                *GetNameSafe(Asset));
        }
    };

    if (IsInGameThread())
    {
        ApplyOnGameThread();
    }
    else
    {
        AsyncTask(ENamedThreads::GameThread, ApplyOnGameThread);
    }
}
