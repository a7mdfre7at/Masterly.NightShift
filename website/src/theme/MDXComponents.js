import React, {useEffect, useRef, useState} from 'react';
import MDXComponents from '@theme-original/MDXComponents';

/**
 * A Markdown table inside a scroll container (the data table's outline). When the table is wider than the page, the
 * container takes keyboard focus and a name, so keyboard users can scroll it too.
 */
function Table(props) {
  const container = useRef(null);
  const [label, setLabel] = useState(null);

  useEffect(() => {
    const element = container.current;
    if (!element || typeof ResizeObserver === 'undefined') {
      return undefined;
    }
    // Named after its section and column headings, so each scrolling table is a landmark of its own.
    let section = element.previousElementSibling;
    while (section && !/^H[2-6]$/.test(section.tagName)) {
      section = section.previousElementSibling;
    }
    const columns = [...element.querySelectorAll('thead th')].map((cell) => cell.textContent.trim()).filter(Boolean);
    const where = section ? ` in "${section.textContent.trim()}"` : '';
    const name = `Table${where}: ${columns.join(', ') || 'scrolls sideways'}`;
    const check = () => setLabel(element.scrollWidth > element.clientWidth + 1 ? name : null);
    check();
    const observer = new ResizeObserver(check);
    observer.observe(element);
    if (element.firstElementChild) {
      observer.observe(element.firstElementChild);
    }
    return () => observer.disconnect();
  }, []);

  const focusable = label ? {tabIndex: 0, role: 'region', 'aria-label': label} : {};
  return (
    <div ref={container} className="ns-table" {...focusable}>
      <table {...props} />
    </div>
  );
}

export default {
  ...MDXComponents,
  table: Table,
};
