import React, {useCallback, useEffect, useRef, useState} from 'react';
import clsx from 'clsx';
import Icon from './Icon';
import styles from './JobsDemo.module.css';

// A miniature of the dashboard's job list. The jobs and their state changes follow the docs: a fire-and-forget job
// (jobs/fire-and-forget.md), a charge that fails once and is retried (filters/retries.md: retry 1 waits 15 to 44
// seconds), its receipt as a continuation (jobs/continuations.md) and a recurring report (recurring-jobs).
const finalStep = 8;
const stepDelayMs = 700;

// Each timeline entry is [fromStep, state, detail]. The last entry whose fromStep <= step applies.
const jobs = [
  {
    id: '1042',
    method: 'EmailService.SendWelcome(42)',
    timeline: [
      [0, 'enqueued', 'queue: default'],
      [1, 'processing', 'worker 1'],
      [2, 'succeeded', '118 ms'],
    ],
  },
  {
    id: '1043',
    method: 'OrderService.ChargeAsync(1001)',
    timeline: [
      [0, 'enqueued', 'queue: default'],
      [2, 'processing', 'worker 2'],
      [3, 'failed', 'HttpRequestException'],
      [4, 'scheduled', 'retry 1 of 10 in 34 s'],
      [5, 'processing', 'worker 1'],
      [6, 'succeeded', '402 ms'],
    ],
  },
  {
    id: '1044',
    method: 'EmailService.SendReceipt(1001)',
    timeline: [
      [0, 'awaiting', 'after #1043'],
      [6, 'enqueued', 'parent succeeded'],
      [7, 'processing', 'worker 2'],
      [8, 'succeeded', '96 ms'],
    ],
  },
  {
    id: 'daily-report',
    method: 'ReportService.BuildDaily()',
    timeline: [[0, 'scheduled', 'recurring · tomorrow at 06:00']],
  },
];

const stateLabels = {
  enqueued: 'Enqueued',
  scheduled: 'Scheduled',
  awaiting: 'Awaiting',
  processing: 'Processing',
  succeeded: 'Succeeded',
  failed: 'Failed',
};

function stateAt(job, step) {
  let current = job.timeline[0];
  for (const entry of job.timeline) {
    if (entry[0] <= step) {
      current = entry;
    }
  }
  return {state: current[1], detail: current[2]};
}

function prefersReducedMotion() {
  return typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/**
 * A dashboard-like window that plays four jobs through their state changes once it scrolls into view.
 * Every row is rendered from the start, so the animation never shifts the layout.
 */
export default function JobsDemo() {
  const [step, setStep] = useState(0);
  const [running, setRunning] = useState(false);
  const container = useRef(null);
  const started = useRef(false);

  const run = useCallback(() => {
    if (prefersReducedMotion()) {
      setStep(finalStep);
      return;
    }
    setStep(0);
    setRunning(true);
  }, []);

  useEffect(() => {
    if (!running) {
      return undefined;
    }
    if (step >= finalStep) {
      setRunning(false);
      return undefined;
    }
    const timer = setTimeout(() => setStep((current) => current + 1), stepDelayMs);
    return () => clearTimeout(timer);
  }, [running, step]);

  useEffect(() => {
    const element = container.current;
    if (!element || typeof IntersectionObserver === 'undefined') {
      run();
      return undefined;
    }
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting) && !started.current) {
          started.current = true;
          run();
          observer.disconnect();
        }
      },
      {threshold: 0.4},
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [run]);

  const done = step >= finalStep;
  const states = jobs.map((job) => stateAt(job, step));
  const count = (state) => states.filter((entry) => entry.state === state).length;

  return (
    <div className={styles.window} ref={container}>
      <div className={styles.titleBar}>
        <span className={styles.dots} aria-hidden="true">
          <span />
          <span />
          <span />
        </span>
        <span className={styles.path}>/nightshift/jobs</span>
        <button type="button" className={styles.runButton} onClick={run} disabled={running}>
          <Icon name={done ? 'replay' : 'play'} size={14} />
          <span>{done ? 'Replay' : running ? 'Running' : 'Run'}</span>
        </button>
      </div>

      <div className={styles.counters} role="status" aria-live="polite">
        <span className={clsx(styles.counter, styles.processing)}>
          <b>{count('processing')}</b> processing
        </span>
        <span className={clsx(styles.counter, styles.succeeded)}>
          <b>{count('succeeded')}</b> succeeded
        </span>
        <span className={clsx(styles.counter, styles.failed)}>
          <b>{count('failed')}</b> failed
        </span>
        <span className={clsx(styles.counter, styles.scheduled)}>
          <b>{count('scheduled')}</b> scheduled
        </span>
      </div>

      <ol className={styles.list} aria-label="Jobs">
        {jobs.map((job, index) => {
          const {state, detail} = states[index];
          return (
            <li key={job.id} className={styles.row}>
              <span className={styles.jobId}>#{job.id}</span>
              <span className={styles.method}>{job.method}</span>
              <span className={clsx(styles.badge, styles[state])}>{stateLabels[state]}</span>
              <span className={styles.detail}>{detail}</span>
            </li>
          );
        })}
      </ol>

      <div className={styles.footer}>
        <span className={styles.live} aria-hidden="true" />
        <span>{done ? 'All caught up. Retries happened on their own.' : 'Workers are running jobs…'}</span>
      </div>
    </div>
  );
}
