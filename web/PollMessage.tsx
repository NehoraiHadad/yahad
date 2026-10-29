import React from 'react';
import {pollDisplay,pollUnavailable,invalidPollText,pollResultDisplay,observedResultsNotice} from '../src/poll-display';

/** Read-only content: list items, no vote controls or invented results. */
export function PollMessage({question,options,results}:{question:string;options:unknown;results?:unknown}){
  const display=pollDisplay(question,options);
  if(!display)return <p className="poll-limitation" role="status">{invalidPollText}</p>;
  const observed=pollResultDisplay(results,display.poll.options.length);
  return <section className="poll-card" aria-label="סקר לקריאה בלבד" dir="rtl">
    <p className="poll-question" dir="auto">{display.text}</p>
    <ol className="poll-options" aria-label="אפשרויות הסקר">
      {display.poll.options.map((option,index)=><li key={index}><span dir="auto">{option}</span>{observed && <small className="poll-count" aria-label="הצבעות שהתקבלו ופוענחו">{observed.counts[index]}</small>}</li>)}
    </ol>
    <p className="poll-limitation">{observed?observedResultsNotice:pollUnavailable}</p>
  </section>;
}
