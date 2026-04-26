#pragma once

#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "Engine/EngineTypes.h"
#include "UObject/ObjectPtr.h"
#include "ExpressionReceiverComponent.generated.h"

class FSocket;
class USkeletalMeshComponent;

DECLARE_LOG_CATEGORY_EXTERN(LogVHReceiver, Log, All);

DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(
    FOnExpressionFrameReceived,
    int64, SequenceId,
    const FString&, EmotionLabel);

/**
 * VHReceiver — 纯网络/Morph 接收组件（UActorComponent）。
 *
 * Target 网格通过 FComponentReference（组件选择器）或 PreferredTargetMeshName 解析，
 * 不在此暴露会直接内联展开 SkeletalMesh 详情的裸组件指针。
 *
 * 迁移：改 UPROPERTY 后若详情异常，请关编辑器、清 Binaries/Intermediate、重编，
 * 删除并重新添加组件，用「Target Mesh Component」选择器选中 miku_mesh，Compile/Save。
 */
UCLASS(ClassGroup=(VirtualHuman), meta=(BlueprintSpawnableComponent), DisplayName="Expression Receiver")
class VHRECEIVER_API UExpressionReceiverComponent : public UActorComponent
{
    GENERATED_BODY()

public:
    UExpressionReceiverComponent();

    virtual void BeginPlay() override;
    virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;
    virtual void TickComponent(float DeltaTime,
                               ELevelTick TickType,
                               FActorComponentTickFunction* ThisTickFunction) override;

    /**
     * 在详情里用「组件选择器」指定 Owner 上的 USkeletalMeshComponent（例如 miku_mesh）。
     * 使用 UseComponentPicker，不会在下面内联展开整套网格/动画编辑。
     * OtherActor 请留空，表示使用本组件所在 Actor。
     */
    UPROPERTY(EditAnywhere, BlueprintReadOnly, Category="VHReceiver",
        meta=(DisplayName="Target Mesh Component", UseComponentPicker="true",
            AllowedClasses="/Script/Engine.SkeletalMeshComponent"))
    FComponentReference TargetMeshComponentRef;

    /**
     * 当未在 Target Mesh Component 中指定引用时，按此组件名在 Owner 上查找（不区分大小写）。
     * 默认 miku_mesh；不会静默选用 SkeletalMeshComponent_0。
     */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver",
        meta=(DisplayName="Preferred Target Mesh Name"))
    FString PreferredTargetMeshName = TEXT("miku_mesh");

    /**
     * 为 true 时：解析到的 Target 必须与本组件同属一个 Actor，否则拒绝 Morph Apply。
     */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver")
    bool bRequireTargetMeshOwnedBySameActor = true;

    /** BeginPlay 时是否在 TargetMesh 的资产上做一次 morph 存在性探测（与运行时是否成功 SetMorphTarget 无关）。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver|Debug")
    bool bRunMorphProbeOnBeginPlay = true;

    /** TCP 监听端口，需要与 sender 的 --port 保持一致。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver")
    int32 ListenPort = 7001;

    /** 某些模型左右眨眼名称和协议左右定义相反时可启用。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver")
    bool bSwapWinkLeftRight = false;

    /** 口型附加权重缩放，最终权重仍会被限制到 [0,1]。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver")
    float PhonemeWeightScale = 1.0f;

    /** 为 true 时会把上一帧未继续出现的 Morph 自动清零。调试时可关断以排除「被每帧清空」的干扰。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver")
    bool bClearMorphsEachFrame = true;

    /**
     * 超过该时间没有新帧时自动回 neutral。
     * 调试建议 >= 5s；设为 0 或负数则禁用自动重置。
     */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver")
    float FaceResetTimeoutSecs = 5.0f;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver")
    float ClientIdleTimeoutSecs = 2.0f;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver")
    float PendingAcceptIdleForceCloseSecs = 0.35f;

    /** 在 Output Log 中打印每帧摘要。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver|Debug")
    bool bLogFramesToOutput = true;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver|Debug", meta=(ClampMin="1", UIMin="1"))
    int32 LogFrameStride = 1;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver|Debug", meta=(ClampMin="0", UIMin="0"))
    int32 LogJsonPreviewChars = 200;

    /**
     * 为 true 时：StartListening/CloseAll/CloseClient/Tick/TryAccept/Read 等会打生命周期日志（含 Listen/Client 指针、端口）。
     * 用于追查「谁关掉了 listener」与 WSA 10061。
     */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver|Debug")
    bool bVerboseLifecycleLog = true;

    /** 为 true 时，每次成功调用 SetMorphTarget 时打印 [VHReceiver][Apply] 日志（建议联调时开启）。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver|Debug")
    bool bLogEachMorphApply = true;

    /** 为 true 时，在 [Parse] 中逐条打印 protocol key -> 映射后 morph 名 -> 数值（受帧日志 stride 影响）。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver|Debug")
    bool bLogParseMorphMapping = true;

    /** 为 true 时，bClearMorphsEachFrame / ResetFaceMorphs 清零 morph 时打印 [VHReceiver][Clear]。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver|Debug")
    bool bLogMorphClearOperations = true;

    /**
     * 为 true 时：忽略 JSON 中的 blendshape，仅按 sequence_id 在 笑い / ウィンク / びっくり 间轮换并置 1.0。
     * 用于验证「管线是否持续 Apply」，与情绪映射无关。
     */
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver|Debug")
    bool bDebugMinimalMorphCycle = false;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver")
    TMap<FString, FString> MorphNameOverrides;

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="VHReceiver")
    TMap<FString, FString> PhonemeNameOverrides;

    UPROPERTY(BlueprintAssignable, Category="VHReceiver")
    FOnExpressionFrameReceived OnFrameReceived;

    UFUNCTION(BlueprintCallable, Category="VHReceiver")
    void RefreshMapping();

    UFUNCTION(BlueprintCallable, Category="VHReceiver")
    void ResetFaceMorphs();

    UFUNCTION(BlueprintCallable, Category="VHReceiver")
    void StartListening();

    UFUNCTION(BlueprintCallable, Category="VHReceiver")
    void StopListening();

    UFUNCTION(BlueprintPure, Category="VHReceiver")
    bool IsClientConnected() const;

    UFUNCTION(BlueprintPure, Category="VHReceiver")
    FString GetLastEmotionLabel() const { return LastEmotionLabel; }

    UFUNCTION(BlueprintPure, Category="VHReceiver")
    int64 GetLastSequenceId() const { return LastSequenceId; }

    /** 已成功 ApplyFrame（解析并驱动 morph）的累计次数，用于联调统计。 */
    UFUNCTION(BlueprintPure, Category="VHReceiver|Debug")
    int64 GetFramesAppliedTotal() const { return FramesAppliedTotal; }

    /** BeginPlay 解析后的目标骨骼网格组件名；未解析成功则为空。 */
    UFUNCTION(BlueprintPure, Category="VHReceiver|Debug")
    FString GetResolvedTargetMeshName() const;

    /** BeginPlay 解析后的 USkeletalMeshComponent；可为 nullptr。 */
    UFUNCTION(BlueprintPure, Category="VHReceiver|Debug")
    USkeletalMeshComponent* GetResolvedTargetMesh() const;

    /**
     * 调试：与 live 相同链路调用 SetMorphTargetSafe（GameThread）。
     * 用于验证 Target 绑定与 apply 管线，不涉及网络/JSON。
     */
    UFUNCTION(BlueprintCallable, Category="VHReceiver|Debug")
    void DebugApplyMorph(const FString& MorphName, float Value);

private:
    FSocket* ListenSocket = nullptr;
    FSocket* ClientSocket = nullptr;
    TArray<uint8> ReceiveBuffer;

    TMap<FString, FString> EffectiveMorphMap;
    TMap<FString, FString> EffectivePhonemeMap;
    TSet<FName> PrevDrivenMorphs;
    TSet<FName> VerifiedMorphNames;
    TSet<FName> WarnedMissingMorphs;

    double LastFrameReceivedTime  = -1.0;
    double LastSocketActivityTime = -1.0;
    FString LastEmotionLabel;
    int64 LastSequenceId = 0;
    bool bWarnedMissingTargetMesh = false;
    bool bLoggedFirstFrameSample = false;
    int64 FramesAppliedTotal = 0;

    /** BeginPlay 由 TargetMeshComponentRef / PreferredTargetMeshName 解析得到；不序列化到编辑器裸指针。 */
    TObjectPtr<USkeletalMeshComponent> ResolvedTargetMesh = nullptr;

    bool bMorphApplyBlockedByCrossActorTarget = false;

    double LastListenSocketNullLogTime = -1.0;

    static TMap<FString, FString> BuildDefaultMorphMap(bool bInSwapWinkLeftRight);
    static TMap<FString, FString> BuildDefaultPhonemeMap();

    void LogLifecycle(const TCHAR* CallSite, const TCHAR* Detail = nullptr) const;

    void LogSkeletalMeshCandidatesOnOwner(AActor* OwnerActor) const;
    static FString BuildCandidateNameList(const TArray<USkeletalMeshComponent*>& Meshes);
    static USkeletalMeshComponent* FindOwnerSkeletalMeshByPreferredName(
        AActor* OwnerActor,
        const FString& PreferredIn);

    void ResolveAndLogTargetMesh();
    void ValidateTargetMeshOwnershipAfterResolve();

    bool CanApplyMorphsThisFrame() const;

    void TryAcceptConnection();
    void ReadAndProcessData();
    bool ApplyFrame(const FString& JsonLine);
    void CloseClient(const TCHAR* Reason);
    void CloseAll(const TCHAR* Reason);

    FString ResolveMorphName(const FString& ProtocolKey) const;
    FString ResolvePhonemeMorphName(const FString& PhonemeKey) const;
    bool HasMorphTarget(FName MorphName);
    void SetMorphTargetSafe(FName MorphName, float Weight, const TCHAR* Context);
};
