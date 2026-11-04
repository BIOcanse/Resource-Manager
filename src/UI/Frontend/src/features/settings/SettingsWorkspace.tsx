import { onPageLeave } from "../../app/navigation/pageHost.tsx";
import { SettingsPage } from "./SettingsPage";
import type { SettingsStore } from "../../stores/settingsStore";
import { useSettingsPanelBindings } from "./SettingsPanelContext.tsx";

interface SettingsWorkspaceProps {
  settings: SettingsStore;
}

export function SettingsWorkspace(props: SettingsWorkspaceProps) {
  const settings = props.settings;
  // Discard unsaved draft changes when this visit ends, including retained-page hiding.
  onPageLeave(settings.discardDraftChanges);
  // Resolve bindings during component creation; event-time JSX getters have no provider context.
  const bindings = useSettingsPanelBindings();
  return (
    <SettingsPage
      bindings={bindings}
      activeSection={settings.activeSection()}
      onSectionChange={settings.setActiveSection}
    />
  );
}
