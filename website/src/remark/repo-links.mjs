// The pages in docs/ are written to be read on GitHub too, so some of them link to files outside the published docs:
// source files, CONTRIBUTING.md, or the design notes in docs/architecture. On the site those relative links would be
// broken, so this plugin points each one at the file on GitHub instead. Links between published pages are left alone
// for Docusaurus to resolve (and to check).
import fs from 'node:fs';
import path from 'node:path';

const schemePattern = /^[a-z][a-z0-9+.-]*:/i;

/**
 * @param {{docsDir: string, repoRoot: string, repositoryUrl: string, branch: string, unpublished: string[]}} options
 *   unpublished: folders under docsDir that the site excludes, relative to docsDir.
 */
export default function repoLinks({docsDir, repoRoot, repositoryUrl, branch, unpublished}) {
  function isPublished(target) {
    const relative = path.relative(docsDir, target);
    if (relative.startsWith('..') || path.isAbsolute(relative)) {
      return false;
    }
    return !unpublished.some((folder) => relative === folder || relative.startsWith(folder + path.sep));
  }

  function rewrite(node, filePath) {
    const url = node.url;
    if (!url || url.startsWith('#') || url.startsWith('/') || schemePattern.test(url)) {
      return;
    }
    const hashIndex = url.indexOf('#');
    const targetPart = hashIndex === -1 ? url : url.slice(0, hashIndex);
    const hash = hashIndex === -1 ? '' : url.slice(hashIndex);
    const target = path.resolve(path.dirname(filePath), decodeURI(targetPart));
    if (isPublished(target)) {
      return;
    }
    const isFolder = targetPart.endsWith('/') || (fs.existsSync(target) && fs.statSync(target).isDirectory());
    const repoPath = path.relative(repoRoot, target).split(path.sep).join('/');
    node.url = `${repositoryUrl}/${isFolder ? 'tree' : 'blob'}/${branch}/${repoPath}${hash}`;
  }

  function visit(node, filePath) {
    if (node.type === 'link' || node.type === 'definition') {
      rewrite(node, filePath);
    }
    if (node.children) {
      for (const child of node.children) {
        visit(child, filePath);
      }
    }
  }

  return (tree, file) => {
    if (file.path) {
      visit(tree, file.path);
    }
  };
}
