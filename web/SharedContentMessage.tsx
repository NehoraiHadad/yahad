import React from 'react';
import {sharedText} from '../src/rich-content';

/** Plain text only: no vCard import, embedded map, remote image or URL action. */
export function SharedContentMessage({text}:{text:string}){
 return <p className="message-text" dir="auto">{sharedText(text)||'פרטי ההודעה אינם זמינים לתצוגה.'}</p>;
}
