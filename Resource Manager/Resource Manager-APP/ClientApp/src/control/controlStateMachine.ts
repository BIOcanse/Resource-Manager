import type { ControlSetting, ControlApplyOutcome } from "./controlTypes.ts";

/**
 * 控制面的状态机：期望状态和实际状态对不对得上。
 *
 * 纯函数，不持有状态。期望状态由后端持久化（用户设一次就该一直维持），
 * 界面上正在编辑的那份是本地副本；这里负责算出每一项现在处于什么状态，
 * **而不是另外再维护一份"正在应用"的标志位** —— 标志位会和事实不同步，
 * 算出来的不会。
 */
export type ControlItemStatus =
  /** 用户没设过这一项。 */
  | "unset"
  /** 改了还没提交。 */
  | "edited"
  /** 提交了，还在等回执。 */
  | "applying"
  /** 写进去了。 */
  | "applied"
  /** 这台机器上这项控不了。 */
  | "unsupported"
  /** 试了但没成功。 */
  | "failed";

export interface ControlItemView {
  capabilityId: string;
  status: ControlItemStatus;
  /** 不是 applied 时的说明。applied 时为 null。 */
  message: string | null;
}

function sameValue(left: ControlSetting, right: ControlSetting): boolean {
  if (left.number !== right.number || left.toggle !== right.toggle) {
    return false;
  }
  const leftCurve = left.curve ?? null;
  const rightCurve = right.curve ?? null;
  if (leftCurve === null || rightCurve === null) {
    return leftCurve === rightCurve;
  }
  return leftCurve.length === rightCurve.length
    && leftCurve.every((point, index) =>
      point.temperatureCelsius === rightCurve[index].temperatureCelsius
      && point.percent === rightCurve[index].percent);
}

function find(
  settings: readonly ControlSetting[],
  capabilityId: string
): ControlSetting | undefined {
  return settings.find((setting) => setting.capabilityId === capabilityId);
}

/**
 * 算出一项现在的状态。
 *
 * 判断顺序是有讲究的：**本地改动优先于回执**。
 * 用户刚拖完滑块还没提交时，上一次的回执说的是旧值的事，不该拿来说新值已应用。
 */
export function controlItemStatusOf(
  capabilityId: string,
  edited: readonly ControlSetting[],
  saved: readonly ControlSetting[],
  outcomes: readonly ControlApplyOutcome[],
  submitting: boolean
): ControlItemView {
  const editedSetting = find(edited, capabilityId);
  const savedSetting = find(saved, capabilityId);

  if (editedSetting && (!savedSetting || !sameValue(editedSetting, savedSetting))) {
    return {
      capabilityId,
      status: submitting ? "applying" : "edited",
      message: null
    };
  }
  if (submitting) {
    return { capabilityId, status: "applying", message: null };
  }
  if (!savedSetting) {
    return { capabilityId, status: "unset", message: null };
  }

  const outcome = outcomes.find((entry) => entry.capabilityId === capabilityId);
  if (!outcome) {
    // 存下来了但这一轮回执里没有它 —— 还没施加过，不能报成已应用。
    return { capabilityId, status: "applying", message: null };
  }
  if (outcome.status === "applied") {
    return { capabilityId, status: "applied", message: null };
  }
  return {
    capabilityId,
    status: outcome.status === "unsupported" ? "unsupported" : "failed",
    message: outcome.message
  };
}

/**
 * 这次提交要发哪些项。
 *
 * 发的是**整个对象的设定**而不是增量：后端那边一次替换一个对象的全部设定，
 * 这样不会出现"删掉一项"没法表达的问题。这里只负责判断有没有必要发。
 */
export function hasPendingChanges(
  edited: readonly ControlSetting[],
  saved: readonly ControlSetting[]
): boolean {
  if (edited.length !== saved.length) {
    return true;
  }
  return edited.some((setting) => {
    const previous = find(saved, setting.capabilityId);
    return !previous || !sameValue(setting, previous);
  });
}
