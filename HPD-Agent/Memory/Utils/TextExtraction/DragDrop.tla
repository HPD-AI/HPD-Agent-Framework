---- MODULE DragDrop ----
EXTENDS FiniteSets, TLC

CONSTANTS NodeId, None, InitNodes
VARIABLES Nodes, Parent, DragSrc, DragDst

TypeInvariant ==
  /\ Parent \subseteq Nodes \times Nodes
  /\ DragSrc \in Nodes \cup {None}
  /\ DragDst \in Nodes \cup {None}

Init ==
  /\ Nodes = InitNodes
  /\ Parent = {}
  /\ DragSrc = None
  /\ DragDst = None

DragStart(n) ==
  /\ n \in Nodes
  /\ DragSrc' = n
  /\ DragDst' = None
  /\ UNCHANGED <<Parent, Nodes>>

DragOver(n) ==
  /\ DragSrc # None
  /\ n \in Nodes
  /\ DragDst' = n
  /\ UNCHANGED <<Parent, DragSrc, Nodes>>

Drop(n) ==
  /\ DragSrc \in Nodes
  /\ n \in Nodes
  /\ Parent' = (Parent \ { <<DragSrc, p>>: p \in Nodes }) \cup { <<DragSrc, n>> }
  /\ DragSrc' = None
  /\ DragDst' = None
  /\ UNCHANGED Nodes

CancelDrag ==
  /\ DragSrc # None
  /\ DragSrc' = None
  /\ DragDst' = None
  /\ UNCHANGED <<Parent, Nodes>>

Next ==
  \/ \E n \in Nodes: DragStart(n)
  \/ \E n \in Nodes: DragOver(n)
  \/ \E n \in Nodes: Drop(n)
  \/ CancelDrag

Spec == Init /\ [][Next]_<<Nodes, Parent, DragSrc, DragDst>>

=============================================================================
