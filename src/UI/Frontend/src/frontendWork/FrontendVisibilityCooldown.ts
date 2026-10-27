/** Host/document visibility gates sampling only. A transient hide leaves demand untouched. */
export class FrontendVisibilityCooldown {
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor(
    private readonly delayMs: number,
    private readonly publish: (visible: boolean) => void,
    private readonly schedule: (run: () => void, delayMs: number) => ReturnType<typeof setTimeout> =
      (run, delay) => setTimeout(run, delay),
    private readonly cancel: (timer: ReturnType<typeof setTimeout>) => void = timer => clearTimeout(timer)
  ) {}

  setVisible(visible: boolean): void {
    if (visible) {
      this.dispose();
      this.publish(true);
    } else if (this.delayMs === 0) {
      this.publish(false);
    } else if (this.timer === undefined) {
      this.timer = this.schedule(() => {
        this.timer = undefined;
        this.publish(false);
      }, this.delayMs);
    }
  }

  dispose(): void {
    if (this.timer !== undefined) this.cancel(this.timer);
    this.timer = undefined;
  }
}
