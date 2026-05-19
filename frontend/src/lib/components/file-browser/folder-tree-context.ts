export type FolderTreeContext = {
  isExpanded: (id: string) => boolean;
  toggleExpanded: (id: string) => void;
  setExpanded: (id: string, expanded: boolean) => void;
};

export const FOLDER_TREE_KEY = Symbol.for('folder-tree-context');
