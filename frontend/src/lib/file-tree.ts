import type { FileItem } from "./types"

export function findFileById(files: FileItem[], id: string): FileItem | null {
  for (const file of files) {
    if (file.id === id) return file
    if (file.children) {
      const found = findFileById(file.children, id)
      if (found) return found
    }
  }
  return null
}

/** Returns the immediate folder id that contains `fileId`, or null if not found / at root. */
export function findAncestorFolderId(files: FileItem[], fileId: string): string | null {
  function walk(items: FileItem[], ancestors: FileItem[]): string | null {
    for (const item of items) {
      if (item.id === fileId) {
        const parent = ancestors[ancestors.length - 1]
        return parent?.id ?? null
      }
      if (item.children?.length) {
        const found = walk(item.children, [...ancestors, item])
        if (found !== null) return found
      }
    }
    return null
  }
  return walk(files, [])
}

export function getPathToFile(files: FileItem[], targetId: string): FileItem[] {
  const path: FileItem[] = []

  function search(items: FileItem[], target: string): boolean {
    for (const item of items) {
      if (item.id === target) {
        path.push(item)
        return true
      }
      if (item.children && search(item.children, target)) {
        path.unshift(item)
        return true
      }
    }
    return false
  }

  search(files, targetId)
  return path
}
