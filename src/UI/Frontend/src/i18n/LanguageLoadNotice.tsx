import { Show } from "solid-js";
import { applyLanguage, currentLanguage, languageLoadError } from "./appTextStore.ts";
import { languageFailureText } from "./languageFailureText.ts";

export function LanguageLoadNotice() {
  const text = () => languageFailureText(currentLanguage());
  return <Show when={languageLoadError()}>{(failure) =>
    <div role="alert" class="observation-state error" data-language-load-error>
      <span>{text().message}</span>
      <button class="secondary" type="button" onClick={() => {
        void applyLanguage(failure().language).catch(() => undefined);
      }}>{text().retry}</button>
    </div>
  }</Show>;
}
