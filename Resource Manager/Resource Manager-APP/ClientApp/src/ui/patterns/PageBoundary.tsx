import {
  ErrorBoundary,
  Show,
  createEffect,
  createSignal,
  onCleanup,
  type JSX
} from "solid-js";
import { RotateCcw } from "lucide-solid";
import { userFacingErrorMessage } from "../../presentation/userFacingText";

export function PageBoundary(props: {
  readonly name: string;
  readonly resetKey?: unknown;
  readonly lastGood?: JSX.Element;
  readonly onRetry?: () => void | Promise<unknown>;
  readonly onError?: (error: unknown) => void;
  readonly children: JSX.Element;
}) {
  let resetBoundary: (() => void) | undefined;

  createEffect(() => {
    props.resetKey;
    resetBoundary?.();
  });
  onCleanup(() => { resetBoundary = undefined; });

  return (
    <ErrorBoundary fallback={(error, reset) => {
      resetBoundary = reset;
      props.onError?.(error);
      return (
        <PageBoundaryFallback
          name={props.name}
          error={error}
          lastGood={props.lastGood}
          onRetry={props.onRetry}
          reset={reset}
        />
      );
    }}>
      {props.children}
    </ErrorBoundary>
  );
}

function PageBoundaryFallback(props: {
  readonly name: string;
  readonly error: unknown;
  readonly lastGood?: JSX.Element;
  readonly onRetry?: () => void | Promise<unknown>;
  readonly reset: () => void;
}) {
  const [retrying, setRetrying] = createSignal(false);
  const [retryError, setRetryError] = createSignal<string | null>(null);
  const message = () => retryError()
    ?? userFacingErrorMessage(props.error, `${props.name}暂时不可用`);

  const retry = async () => {
    if (retrying()) {
      return;
    }
    setRetrying(true);
    setRetryError(null);
    try {
      await props.onRetry?.();
      props.reset();
    } catch (error) {
      setRetryError(userFacingErrorMessage(error, `${props.name}重试失败`));
    } finally {
      setRetrying(false);
    }
  };

  return (
    <section
      ref={(element) => requestAnimationFrame(() => element.focus({ preventScroll: true }))}
      class="page-boundary-fallback"
      role="alert"
      aria-live="assertive"
      tabIndex={-1}
    >
      <div class="page-boundary-message">
        <strong>{props.name}暂时不可用</strong>
        <p>{message()}</p>
        <Show when={props.onRetry}>
          <button class="secondary icon-text-button" type="button" disabled={retrying()} onClick={() => void retry()}>
            <RotateCcw aria-hidden="true" size={16} />
            <span>{retrying() ? "正在重试" : "重试"}</span>
          </button>
        </Show>
      </div>
      <Show when={props.lastGood}>
        <div class="page-boundary-last-good" aria-label={`${props.name}上次可用内容`}>
          {props.lastGood}
        </div>
      </Show>
    </section>
  );
}
