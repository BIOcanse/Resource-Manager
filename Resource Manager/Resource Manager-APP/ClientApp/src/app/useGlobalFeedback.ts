import { createSignal } from "solid-js";
import type {
  ConfirmDialogRecord,
  ConfirmDialogRequest,
  ToastInput,
  ToastItem,
  ToastTone
} from "../components/AppFeedback";
import { uiText } from "../text";
import { userFacingErrorMessage, userFacingMessage } from "../presentation/userFacingText";

export function useGlobalFeedback() {
  const [confirmQueue, setConfirmQueue] = createSignal<ConfirmDialogRecord[]>([]);
  const [toasts, setToasts] = createSignal<ToastItem[]>([]);
  const confirmResolvers = new Map<string, Array<(confirmed: boolean) => void>>();

  function confirmDialog(request: ConfirmDialogRequest): Promise<boolean> {
    const id = crypto.randomUUID();
    const dedupeKey = normalizeConfirmDedupeKey(request);
    const queuedDuplicate = confirmQueue().find((item) =>
      normalizeConfirmDedupeKey(item) === dedupeKey);

    return new Promise((resolve) => {
      if (queuedDuplicate) {
        const resolvers = confirmResolvers.get(queuedDuplicate.id) ?? [];
        confirmResolvers.set(queuedDuplicate.id, [...resolvers, resolve]);
        return;
      }

      confirmResolvers.set(id, [resolve]);
      setConfirmQueue((current) => [
        ...current,
        { ...request, id, dedupeKey }
      ]);
    });
  }

  function resolveConfirmDialog(confirmed: boolean) {
    const current = confirmQueue()[0];
    if (!current) {
      return;
    }

    for (const resolve of confirmResolvers.get(current.id) ?? []) {
      resolve(confirmed);
    }
    confirmResolvers.delete(current.id);
    setConfirmQueue((items) => items.slice(1));
  }

  function showToast(input: ToastInput) {
    const id = crypto.randomUUID();
    const tone = typeof input === "string" ? "info" : input.tone ?? "info";
    const toast: ToastItem = typeof input === "string"
      ? { id, tone, title: uiText.feedback.info, message: userFacingMessage(input, uiText.feedback.info) }
      : {
        id,
        tone,
        title: input.title ?? toastTitleForTone(tone),
        message: input.message ? userFacingMessage(input.message, toastTitleForTone(tone)) : undefined,
        details: input.details?.filter(Boolean)
      };
    setToasts((current) => [toast, ...current].slice(0, 5));
    const durationMs = typeof input === "string"
      ? 4800
      : input.durationMs ?? (tone === "error" ? 9000 : tone === "warning" ? 7000 : 5200);
    window.setTimeout(() => dismissToast(id), durationMs);
  }

  function showErrorToast(error: unknown, fallback: string) {
    showToast({ tone: "error", title: uiText.feedback.error, message: errorMessage(error, fallback) });
  }

  function dismissToast(id: string) {
    setToasts((current) => current.filter((item) => item.id !== id));
  }

  return {
    confirmQueue,
    toasts,
    confirmDialog,
    resolveConfirmDialog,
    showToast,
    showErrorToast,
    dismissToast
  };
}

export function normalizeConfirmDedupeKey(
  request: ConfirmDialogRequest)
{
  const explicitKey = request.dedupeKey?.trim();
  if (explicitKey) {
    return explicitKey;
  }

  return JSON.stringify({
    title: request.title.trim(),
    message: request.message?.trim() ?? "",
    details: request.details?.map((detail) => detail.trim()).filter(Boolean) ?? [],
    tone: request.tone ?? "info",
    confirmLabel: request.confirmLabel?.trim() ?? "",
    cancelLabel: request.cancelLabel?.trim() ?? ""
  });
}

function toastTitleForTone(tone: ToastTone) {
  if (tone === "success") {
    return uiText.feedback.success;
  }
  if (tone === "error") {
    return uiText.feedback.error;
  }
  if (tone === "warning") {
    return uiText.feedback.warning;
  }

  return uiText.feedback.info;
}

function errorMessage(error: unknown, fallback: string) {
  return userFacingErrorMessage(error, fallback);
}
