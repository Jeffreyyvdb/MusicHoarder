/**
 * Command-palette store — owns the open/closed flag for the global Cmd+K
 * "search everywhere" dialog. Mounted once at the `(app)` layout level so the
 * global keyboard shortcut and the AppHeader `⌘K` badge can both drive the same
 * dialog.
 */

let isOpen = $state(false);

function setOpen(value: boolean): void {
  isOpen = value;
}

function toggle(): void {
  isOpen = !isOpen;
}

export const commandPalette = {
  get open() {
    return isOpen;
  },
  set open(value: boolean) {
    isOpen = value;
  },
  setOpen,
  toggle
};
