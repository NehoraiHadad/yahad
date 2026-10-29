import React from 'react';
import {pollDisplay,pollUnavailable,invalidPollText} from '../src/poll-display';

/** Read-only content: list items, no vote controls or invented results. */
export function PollMessage({question,options}:{question:string;options:unknown}){
  const display=pollDisplay(question,options);
  if(!display)return <p className="poll-limitation" role="status">{invalidPollText}</p>;
  return <section className="poll-card" aria-label="סקר לקריאה בלבד" dir="rtl">
    <p className="poll-question" dir="auto">{display.text}</p>
    <ol className="poll-options" aria-label="אפשרויות הסקר">
      {display.poll.options.map((option,index)=><li key={index}><span dir="auto">{option}</span></li>)}
    </ol>
    <p className="poll-limitation">{pollUnavailable}</p>
  </section>;
}
