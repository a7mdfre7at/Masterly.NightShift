// The code theme for both color modes. Every colour is a CSS variable from custom.css (--ns-code-*), which reads the
// MD3 roles, so one theme follows the site's light and dark tokens and each token stays at least 4.5:1 on the code
// surface.

/** @type {import('prism-react-renderer').PrismTheme} */
const md3Prism = {
  plain: {
    color: 'var(--ns-code-plain)',
    backgroundColor: 'var(--ns-code-surface)',
  },
  styles: [
    {types: ['comment', 'prolog', 'doctype', 'cdata'], style: {color: 'var(--ns-code-comment)', fontStyle: 'italic'}},
    {types: ['punctuation', 'operator'], style: {color: 'var(--ns-code-punctuation)'}},
    {types: ['namespace'], style: {color: 'var(--ns-code-plain)'}},
    {types: ['keyword', 'atrule', 'selector', 'important'], style: {color: 'var(--ns-code-keyword)'}},
    {types: ['string', 'char', 'attr-value', 'inserted', 'template-string', 'url'], style: {color: 'var(--ns-code-string)'}},
    {types: ['number', 'boolean', 'constant', 'symbol', 'unit', 'hexcode'], style: {color: 'var(--ns-code-number)'}},
    {types: ['function', 'method', 'function-variable'], style: {color: 'var(--ns-code-function)'}},
    {types: ['class-name', 'builtin', 'maybe-class-name', 'type-definition', 'interface', 'generic'], style: {color: 'var(--ns-code-type)'}},
    {types: ['property', 'tag', 'attr-name', 'property-access', 'deleted'], style: {color: 'var(--ns-code-property)'}},
    {types: ['regex', 'variable', 'parameter'], style: {color: 'var(--ns-code-regex)'}},
    {types: ['italic'], style: {fontStyle: 'italic'}},
    {types: ['bold'], style: {fontWeight: '600'}},
  ],
};

export default md3Prism;
